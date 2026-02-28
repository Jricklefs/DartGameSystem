/**
 * dsa.cpp - Phase 20: Detection Signal Amplification
 *
 * Improves per-camera barrel mask quality, axis fitting, and tip localization
 * BEFORE triangulation sees the data.
 *
 * 4 parts:
 *   1. Temporal barrel accumulation (aggregate across frames)
 *   2. Weighted axis fit (weighted PCA + RANSAC hybrid)
 *   3. Subpixel tip refinement
 *   4. Gradient tip snap (edge intersection)
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <numeric>
#include <cmath>
#include <iostream>
#include <deque>
#include <mutex>

// ============================================================================
// Feature flags (default OFF; sub-flags default ON when DSA active)
// ============================================================================
static bool g_use_dsa = false;
static bool g_dsa_temporal_barrel = true;
static bool g_dsa_weighted_axis = true;
static bool g_dsa_subpixel_tip = true;
static bool g_dsa_gradient_snap = true;

int set_dsa_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseDSA") { g_use_dsa = (value != 0); return 0; }
    if (s == "DSA_EnableTemporalBarrelAccumulation") { g_dsa_temporal_barrel = (value != 0); return 0; }
    if (s == "DSA_EnableWeightedAxisFit") { g_dsa_weighted_axis = (value != 0); return 0; }
    if (s == "DSA_EnableSubpixelTipRefine") { g_dsa_subpixel_tip = (value != 0); return 0; }
    if (s == "DSA_EnableGradientTipSnap") { g_dsa_gradient_snap = (value != 0); return 0; }
    return -1;
}

bool dsa_is_enabled() { return g_use_dsa; }

// ============================================================================
// DSA Parameters
// ============================================================================
static const int DSA_TEMP_FRAME_WINDOW = 3;
static const int DSA_MIN_PIXEL_STABILITY = 2;
static const int DSA_MIN_CLUSTER_SIZE = 8;
static const double DSA_RANSAC_MAX_DIST = 2.0;
static const int DSA_MIN_RANSAC_INLIERS = 25;
static const int DSA_RANSAC_ITERS = 500;
static const int DSA_TIP_SEARCH_RADIUS = 5;
static const double DSA_GRADIENT_THRESH_MULT = 1.2;

// ============================================================================
// Temporal barrel mask storage (per camera)
// ============================================================================
static std::mutex g_dsa_mutex;
static std::map<std::string, std::deque<cv::Mat>> g_barrel_history;  // cam_id -> recent barrel masks

static void store_barrel_mask(const std::string& cam_id, const cv::Mat& mask) {
    std::lock_guard<std::mutex> lock(g_dsa_mutex);
    auto& hist = g_barrel_history[cam_id];
    hist.push_back(mask.clone());
    while ((int)hist.size() > DSA_TEMP_FRAME_WINDOW) {
        hist.pop_front();
    }
}

void dsa_clear_history() {
    std::lock_guard<std::mutex> lock(g_dsa_mutex);
    g_barrel_history.clear();
}

// ============================================================================
// Part 1: Temporal Barrel Accumulation
// ============================================================================
static cv::Mat temporal_accumulate_barrel(const std::string& cam_id, const cv::Mat& current_mask) {
    std::lock_guard<std::mutex> lock(g_dsa_mutex);
    auto it = g_barrel_history.find(cam_id);
    if (it == g_barrel_history.end() || it->second.empty()) {
        return current_mask.clone();
    }
    
    const auto& hist = it->second;
    
    // Build accumulator: count how many frames each pixel appears in
    cv::Mat accum = cv::Mat::zeros(current_mask.size(), CV_32S);
    
    for (const auto& m : hist) {
        if (m.size() == current_mask.size()) {
            cv::Mat bin;
            m.convertTo(bin, CV_32S, 1.0/255.0);
            accum += bin;
        }
    }
    // Add current mask
    {
        cv::Mat bin;
        current_mask.convertTo(bin, CV_32S, 1.0/255.0);
        accum += bin;
    }
    
    // Keep pixels present in >= MIN_PIXEL_STABILITY frames
    cv::Mat union_mask = cv::Mat::zeros(current_mask.size(), CV_8U);
    union_mask.setTo(255, accum >= DSA_MIN_PIXEL_STABILITY);
    
    // Remove isolated small clusters
    cv::Mat labels, stats, centroids;
    int n = cv::connectedComponentsWithStats(union_mask, labels, stats, centroids);
    cv::Mat filtered = cv::Mat::zeros(union_mask.size(), CV_8U);
    for (int i = 1; i < n; i++) {
        if (stats.at<int>(i, cv::CC_STAT_AREA) >= DSA_MIN_CLUSTER_SIZE) {
            filtered.setTo(255, labels == i);
        }
    }
    
    return filtered;
}

// ============================================================================
// Part 2: Weighted Axis Fit (Weighted PCA + RANSAC hybrid)
// ============================================================================
struct DsaAxisResult {
    bool valid = false;
    double vx = 0, vy = 0;
    double x0 = 0, y0 = 0;
    int inlier_count = 0;
    double inlier_ratio = 0.0;
    double elongation = 0.0;
    double axis_stability = 0.0;
    std::vector<cv::Point2f> inliers;
};

static DsaAxisResult weighted_axis_fit(const cv::Mat& barrel_mask, const cv::Mat& diff_gray) {
    DsaAxisResult res;
    
    // Get barrel pixel locations
    std::vector<cv::Point> pts;
    cv::findNonZero(barrel_mask, pts);
    if ((int)pts.size() < DSA_MIN_RANSAC_INLIERS) return res;
    
    // Compute gradient magnitude for weighting
    cv::Mat gx, gy, gmag;
    cv::Sobel(diff_gray, gx, CV_64F, 1, 0, 3);
    cv::Sobel(diff_gray, gy, CV_64F, 0, 1, 3);
    cv::magnitude(gx, gy, gmag);
    
    // Compute centroid
    double cx = 0, cy = 0;
    for (auto& p : pts) { cx += p.x; cy += p.y; }
    cx /= pts.size(); cy /= pts.size();
    
    // Compute weights: gradient magnitude * distance-from-centroid weighting
    std::vector<double> weights(pts.size());
    double max_dist = 0;
    for (size_t i = 0; i < pts.size(); i++) {
        double d = std::sqrt((pts[i].x - cx)*(pts[i].x - cx) + (pts[i].y - cy)*(pts[i].y - cy));
        max_dist = std::max(max_dist, d);
    }
    if (max_dist < 1.0) max_dist = 1.0;
    
    for (size_t i = 0; i < pts.size(); i++) {
        double gw = 1.0;
        if (pts[i].y >= 0 && pts[i].y < gmag.rows && pts[i].x >= 0 && pts[i].x < gmag.cols) {
            gw = gmag.at<double>(pts[i].y, pts[i].x) + 1.0;
        }
        double dist = std::sqrt((pts[i].x - cx)*(pts[i].x - cx) + (pts[i].y - cy)*(pts[i].y - cy));
        double dw = 0.5 + 0.5 * (dist / max_dist);  // favor pixels further from centroid (along barrel)
        weights[i] = gw * dw;
    }
    
    // Weighted PCA
    double sum_w = 0;
    double wmx = 0, wmy = 0;
    for (size_t i = 0; i < pts.size(); i++) {
        wmx += weights[i] * pts[i].x;
        wmy += weights[i] * pts[i].y;
        sum_w += weights[i];
    }
    if (sum_w < 1e-9) return res;
    wmx /= sum_w; wmy /= sum_w;
    
    double cxx = 0, cxy = 0, cyy = 0;
    for (size_t i = 0; i < pts.size(); i++) {
        double dx = pts[i].x - wmx;
        double dy = pts[i].y - wmy;
        cxx += weights[i] * dx * dx;
        cxy += weights[i] * dx * dy;
        cyy += weights[i] * dy * dy;
    }
    cxx /= sum_w; cxy /= sum_w; cyy /= sum_w;
    
    double trace = cxx + cyy;
    double det = cxx * cyy - cxy * cxy;
    double disc = std::sqrt(std::max(0.0, trace*trace/4.0 - det));
    double lam1 = trace/2.0 + disc;
    double lam2 = trace/2.0 - disc;
    
    if (lam1 < 1e-6) return res;
    
    double evx = cxy;
    double evy = lam1 - cxx;
    double evlen = std::sqrt(evx*evx + evy*evy);
    if (evlen < 1e-6) { evx = 1.0; evy = 0.0; evlen = 1.0; }
    evx /= evlen; evy /= evlen;
    
    res.elongation = (lam2 > 1e-6) ? std::sqrt(lam1 / lam2) : 100.0;
    
    // RANSAC refinement on the weighted PCA direction
    int best_inliers = 0;
    double best_vx = evx, best_vy = evy, best_x0 = wmx, best_y0 = wmy;
    
    unsigned int seed = 12345;
    auto rng = [&seed]() -> int {
        seed = seed * 1103515245 + 12345;
        return (int)((seed >> 16) & 0x7FFF);
    };
    
    int N = (int)pts.size();
    for (int iter = 0; iter < DSA_RANSAC_ITERS; iter++) {
        int i1 = rng() % N;
        int i2 = rng() % N;
        if (i1 == i2) continue;
        
        double dx = pts[i2].x - pts[i1].x;
        double dy = pts[i2].y - pts[i1].y;
        double len = std::sqrt(dx*dx + dy*dy);
        if (len < 3.0) continue;
        
        double nx = -dy / len;
        double ny = dx / len;
        
        int count = 0;
        for (int k = 0; k < N; k++) {
            double dist = std::abs(nx * (pts[k].x - pts[i1].x) + ny * (pts[k].y - pts[i1].y));
            if (dist <= DSA_RANSAC_MAX_DIST) count++;
        }
        
        if (count > best_inliers) {
            best_inliers = count;
            best_vx = dx / len;
            best_vy = dy / len;
            best_x0 = (pts[i1].x + pts[i2].x) / 2.0;
            best_y0 = (pts[i1].y + pts[i2].y) / 2.0;
        }
    }
    
    if (best_inliers < DSA_MIN_RANSAC_INLIERS) return res;
    
    // Collect inliers for final refit
    double nx = -best_vy, ny = best_vx;
    std::vector<cv::Point2f> inliers;
    for (int k = 0; k < N; k++) {
        double dist = std::abs(nx * (pts[k].x - best_x0) + ny * (pts[k].y - best_y0));
        if (dist <= DSA_RANSAC_MAX_DIST) {
            inliers.push_back(cv::Point2f((float)pts[k].x, (float)pts[k].y));
        }
    }
    
    // Refit with inliers (weighted PCA)
    double rmx = 0, rmy = 0;
    for (auto& p : inliers) { rmx += p.x; rmy += p.y; }
    rmx /= inliers.size(); rmy /= inliers.size();
    
    double rcxx = 0, rcxy = 0, rcyy = 0;
    for (auto& p : inliers) {
        double dx2 = p.x - rmx, dy2 = p.y - rmy;
        rcxx += dx2*dx2; rcxy += dx2*dy2; rcyy += dy2*dy2;
    }
    double rtrace = rcxx + rcyy;
    double rdet = rcxx * rcyy - rcxy * rcxy;
    double rdisc = std::sqrt(std::max(0.0, rtrace*rtrace/4.0 - rdet));
    double rlam1 = rtrace/2.0 + rdisc;
    
    if (rlam1 > 1e-6) {
        double rv_x = rcxy, rv_y = rlam1 - rcxx;
        double rv_len = std::sqrt(rv_x*rv_x + rv_y*rv_y);
        if (rv_len > 1e-6) {
            best_vx = rv_x / rv_len;
            best_vy = rv_y / rv_len;
        }
    }
    
    res.valid = true;
    res.vx = best_vx; res.vy = best_vy;
    res.x0 = rmx; res.y0 = rmy;
    res.inlier_count = (int)inliers.size();
    res.inlier_ratio = (double)inliers.size() / (double)N;
    res.inliers = inliers;
    
    // Axis stability: cosine similarity with weighted PCA direction
    res.axis_stability = std::abs(evx * best_vx + evy * best_vy);
    
    return res;
}

// ============================================================================
// Part 3: Subpixel Tip Refinement
// ============================================================================
static Point2f dsa_subpixel_tip(const cv::Mat& diff_gray, Point2f tip, double vx, double vy) {
    int radius = DSA_TIP_SEARCH_RADIUS;
    
    // Compute adaptive gradient threshold
    cv::Scalar mean_val, std_val;
    cv::meanStdDev(diff_gray, mean_val, std_val);
    double grad_thresh = mean_val[0] * DSA_GRADIENT_THRESH_MULT;
    
    // Gradient magnitude
    cv::Mat gx_mat, gy_mat, gmag;
    cv::Sobel(diff_gray, gx_mat, CV_64F, 1, 0, 3);
    cv::Sobel(diff_gray, gy_mat, CV_64F, 0, 1, 3);
    cv::magnitude(gx_mat, gy_mat, gmag);
    
    // Sample along axis ±normal in a small region around tip
    double best_val = 0;
    double best_x = tip.x, best_y = tip.y;
    
    for (int along = -radius; along <= radius; along++) {
        for (int perp = -radius; perp <= radius; perp++) {
            double px = tip.x + along * vx + perp * (-vy);
            double py = tip.y + along * vy + perp * vx;
            
            int ix = (int)std::round(px);
            int iy = (int)std::round(py);
            if (ix < 1 || ix >= gmag.cols - 1 || iy < 1 || iy >= gmag.rows - 1) continue;
            
            double g = gmag.at<double>(iy, ix);
            if (g < grad_thresh) continue;
            
            // Weight by proximity to axis (perpendicular falloff) and along-axis position toward tip
            double perp_w = std::exp(-perp * perp / 4.0);
            double along_w = std::exp(-(along * along) / (radius * radius * 2.0));
            double w = g * perp_w * along_w;
            
            if (w > best_val) {
                best_val = w;
                best_x = px;
                best_y = py;
            }
        }
    }
    
    // Quadratic subpixel refinement around best point
    int bx = (int)std::round(best_x);
    int by = (int)std::round(best_y);
    if (bx >= 1 && bx < gmag.cols - 1 && by >= 1 && by < gmag.rows - 1) {
        double v01 = gmag.at<double>(by, bx - 1);
        double v11 = gmag.at<double>(by, bx);
        double v21 = gmag.at<double>(by, bx + 1);
        double v10 = gmag.at<double>(by - 1, bx);
        double v12 = gmag.at<double>(by + 1, bx);
        
        double dx_den = 2.0 * (v01 - 2*v11 + v21);
        double dy_den = 2.0 * (v10 - 2*v11 + v12);
        
        double sub_dx = (std::abs(dx_den) > 1e-6) ? -(v21 - v01) / dx_den : 0.0;
        double sub_dy = (std::abs(dy_den) > 1e-6) ? -(v12 - v10) / dy_den : 0.0;
        
        sub_dx = std::max(-0.5, std::min(0.5, sub_dx));
        sub_dy = std::max(-0.5, std::min(0.5, sub_dy));
        
        best_x = bx + sub_dx;
        best_y = by + sub_dy;
    }
    
    return Point2f(best_x, best_y);
}

// ============================================================================
// Part 4: Gradient Tip Snap (Edge Intersection)
// ============================================================================
static Point2f gradient_tip_snap(const cv::Mat& diff_gray, Point2f tip, double vx, double vy) {
    // Find strongest perpendicular edge crossing axis near tip
    cv::Mat gx_mat, gy_mat, gmag;
    cv::Sobel(diff_gray, gx_mat, CV_64F, 1, 0, 3);
    cv::Sobel(diff_gray, gy_mat, CV_64F, 0, 1, 3);
    cv::magnitude(gx_mat, gy_mat, gmag);
    
    // Normal to axis
    double nx = -vy, ny = vx;
    
    double best_edge = 0;
    double snap_x = tip.x, snap_y = tip.y;
    
    // Search along the axis direction near the tip
    for (int step = -10; step <= 5; step++) {
        double ax = tip.x + step * vx;
        double ay = tip.y + step * vy;
        
        // Sample perpendicular edge crossings
        double edge_sum = 0;
        int count = 0;
        for (int p = -3; p <= 3; p++) {
            double px = ax + p * nx;
            double py = ay + p * ny;
            int ix = (int)std::round(px);
            int iy = (int)std::round(py);
            if (ix >= 0 && ix < gmag.cols && iy >= 0 && iy < gmag.rows) {
                // Weight by perpendicular gradient component
                double gx_v = gx_mat.at<double>(iy, ix);
                double gy_v = gy_mat.at<double>(iy, ix);
                double perp_grad = std::abs(gx_v * nx + gy_v * ny);
                edge_sum += perp_grad;
                count++;
            }
        }
        
        if (count > 0) {
            double avg_edge = edge_sum / count;
            if (avg_edge > best_edge) {
                best_edge = avg_edge;
                snap_x = ax;
                snap_y = ay;
            }
        }
    }
    
    return Point2f(snap_x, snap_y);
}

// Thread-local storage for last DSA result per camera
static thread_local DsaResult g_last_dsa_result;

DsaResult dsa_get_last_result() { return g_last_dsa_result; }

// ============================================================================
// Main DSA entry point
// ============================================================================
DsaResult run_dsa(
    const std::string& cam_id,
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    DetectionResult& det,
    Point2f board_center)
{
    DsaResult result;
    
    if (!g_use_dsa || !det.tip) {
        g_last_dsa_result = result;
        return result;
    }
    
    // Convert to grayscale
    cv::Mat gray_curr, gray_prev;
    if (current_frame.channels() == 3) {
        cv::cvtColor(current_frame, gray_curr, cv::COLOR_BGR2GRAY);
    } else {
        gray_curr = current_frame.clone();
    }
    if (previous_frame.channels() == 3) {
        cv::cvtColor(previous_frame, gray_prev, cv::COLOR_BGR2GRAY);
    } else {
        gray_prev = previous_frame.clone();
    }
    
    // Differential image
    cv::Mat diff;
    cv::absdiff(gray_curr, gray_prev, diff);
    
    // Record before metrics
    result.barrel_pixel_count_before = det.barrel_pixel_count;
    result.inlier_ratio_before = det.ransac_inlier_ratio;
    
    // Compute initial Q_before
    double inlier_r = det.ransac_inlier_ratio;
    double bp_score = std::min(1.0, det.barrel_pixel_count / 500.0);
    result.Q_before = 0.30 * inlier_r + 0.25 * bp_score + 0.20 * 0.5 + 0.15 * std::min(1.0, det.barrel_aspect_ratio / 5.0) + 0.10 * 0.5;
    
    // Get the existing motion mask as barrel proxy
    cv::Mat barrel_mask;
    if (!det.motion_mask.empty()) {
        barrel_mask = det.motion_mask.clone();
    } else {
        // Create barrel mask from diff threshold
        cv::threshold(diff, barrel_mask, 20, 255, cv::THRESH_BINARY);
    }
    
    // ---------------------------------------------------------------
    // Part 1: Temporal Barrel Accumulation
    // ---------------------------------------------------------------
    cv::Mat enhanced_mask = barrel_mask;
    if (g_dsa_temporal_barrel) {
        enhanced_mask = temporal_accumulate_barrel(cam_id, barrel_mask);
        // Store current mask for future frames
    }
    // Always store (outside lock since store_barrel_mask has its own lock)
    store_barrel_mask(cam_id, barrel_mask);
    
    int new_barrel_count = cv::countNonZero(enhanced_mask);
    result.barrel_pixel_count_after = new_barrel_count;
    det.barrel_pixel_count = new_barrel_count;
    
    // ---------------------------------------------------------------
    // Part 2: Weighted Axis Fit
    // ---------------------------------------------------------------
    double old_vx = det.pca_line ? det.pca_line->vx : 0;
    double old_vy = det.pca_line ? det.pca_line->vy : 0;
    
    DsaAxisResult axis;
    if (g_dsa_weighted_axis && new_barrel_count >= DSA_MIN_RANSAC_INLIERS) {
        axis = weighted_axis_fit(enhanced_mask, diff);
        
        if (axis.valid) {
            // Ensure direction points toward board center
            double to_cx = board_center.x - axis.x0;
            double to_cy = board_center.y - axis.y0;
            if (axis.vx * to_cx + axis.vy * to_cy < 0) {
                axis.vx = -axis.vx;
                axis.vy = -axis.vy;
            }
            
            // Update detection result
            if (det.pca_line) {
                det.pca_line->vx = axis.vx;
                det.pca_line->vy = axis.vy;
                det.pca_line->x0 = axis.x0;
                det.pca_line->y0 = axis.y0;
                det.pca_line->elongation = axis.elongation;
                det.pca_line->method = det.pca_line->method + "+dsa_axis";
            }
            
            det.ransac_inlier_ratio = axis.inlier_ratio;
            result.inlier_ratio_after = axis.inlier_ratio;
            result.axis_stability_score = axis.axis_stability;
            result.elongation_score = std::min(1.0, axis.elongation / 5.0);
            
            // Compute axis direction delta
            if (old_vx != 0 || old_vy != 0) {
                double dot = old_vx * axis.vx + old_vy * axis.vy;
                dot = std::max(-1.0, std::min(1.0, dot));
                result.axis_direction_delta_deg = std::acos(std::abs(dot)) * 180.0 / CV_PI;
            }
        }
    }
    if (!axis.valid) {
        result.inlier_ratio_after = result.inlier_ratio_before;
    }
    
    // ---------------------------------------------------------------
    // Part 3 & 4: Tip refinement
    // ---------------------------------------------------------------
    Point2f old_tip = *det.tip;
    double tip_vx = det.pca_line ? det.pca_line->vx : 0;
    double tip_vy = det.pca_line ? det.pca_line->vy : 1;
    
    Point2f refined_tip = old_tip;
    
    if (g_dsa_subpixel_tip && det.pca_line) {
        refined_tip = dsa_subpixel_tip(diff, refined_tip, tip_vx, tip_vy);
    }
    
    if (g_dsa_gradient_snap && det.pca_line) {
        // Only snap if barrel is weak (< 200 pixels) but axis is reliable
        bool weak_barrel = (new_barrel_count < 200);
        bool axis_reliable = axis.valid && axis.inlier_ratio > 0.3;
        if (weak_barrel && axis_reliable) {
            refined_tip = gradient_tip_snap(diff, refined_tip, tip_vx, tip_vy);
        }
    }
    
    // Bounds check
    if (refined_tip.x >= 0 && refined_tip.x < current_frame.cols &&
        refined_tip.y >= 0 && refined_tip.y < current_frame.rows) {
        det.tip = refined_tip;
        det.method = det.method + "+dsa";
    }
    
    double dx = refined_tip.x - old_tip.x;
    double dy = refined_tip.y - old_tip.y;
    result.tip_shift_px = std::sqrt(dx*dx + dy*dy);
    
    // Compute gradient strength at tip for quality scoring
    {
        cv::Mat gx, gy, gmag;
        cv::Sobel(diff, gx, CV_64F, 1, 0, 3);
        cv::Sobel(diff, gy, CV_64F, 0, 1, 3);
        cv::magnitude(gx, gy, gmag);
        int tx = (int)std::round(refined_tip.x);
        int ty = (int)std::round(refined_tip.y);
        if (tx >= 0 && tx < gmag.cols && ty >= 0 && ty < gmag.rows) {
            // Normalize gradient strength
            cv::Scalar mean_g, std_g;
            cv::meanStdDev(gmag, mean_g, std_g);
            double g_at_tip = gmag.at<double>(ty, tx);
            result.tip_gradient_strength = (mean_g[0] > 1e-6) ? std::min(1.0, g_at_tip / (mean_g[0] * 3.0)) : 0.5;
        }
    }
    
    // ---------------------------------------------------------------
    // Updated Quality Score
    // ---------------------------------------------------------------
    double norm_inlier = std::min(1.0, result.inlier_ratio_after);
    double bp_s = std::min(1.0, result.barrel_pixel_count_after / 500.0);
    double axis_s = axis.valid ? result.axis_stability_score : 0.5;
    double elong_s = axis.valid ? result.elongation_score : std::min(1.0, det.barrel_aspect_ratio / 5.0);
    double tip_gs = result.tip_gradient_strength;
    
    result.Q_after = 0.30 * norm_inlier + 0.25 * bp_s + 0.20 * axis_s + 0.15 * elong_s + 0.10 * tip_gs;
    result.Q_after = std::max(0.0, std::min(1.0, result.Q_after));
    
    result.applied = true;
    g_last_dsa_result = result;
    return result;
}

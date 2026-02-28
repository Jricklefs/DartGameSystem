/**
 * iqdl.cpp - Phase 17: Image Quality + Differential Localization
 *
 * Enhanced per-camera tip detection via:
 *   Step 3: Dart-only differential cleanup (better binary mask)
 *   Step 4: RANSAC shaft axis fit on Canny edges
 *   Step 5: Subpixel tip localization via gradient peak
 *   Step 6: Per-camera confidence weight
 *   Step 7: Fallback to legacy if quality too low
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <numeric>
#include <cmath>
#include <iostream>

// ============================================================================
// IQDL Parameters
// ============================================================================
static const double IQDL_GAUSS_BLUR_SIGMA = 1.2;
static const double IQDL_DIFF_CLIP_PERCENTILE = 99.5;
static const int    IQDL_MORPH_OPEN_K = 3;
static const int    IQDL_MORPH_CLOSE_K = 5;
static const int    IQDL_MIN_DART_AREA_PX = 40;
static const int    IQDL_MIN_AXIS_LENGTH_PX = 15;
static const int    IQDL_MAX_BLOB_COUNT = 6;
static const int    IQDL_CANNY_LOW = 40;
static const int    IQDL_CANNY_HIGH = 120;
static const int    IQDL_RANSAC_ITERS = 400;
static const double IQDL_INLIER_DIST_PX = 3.0;
static const int    IQDL_MIN_INLIERS = 15;
static const int    IQDL_TIP_ROI_SIZE = 31;
static const int    IQDL_SUBPIX_WIN = 7;
static const int    IQDL_SUBPIX_MAX_ITERS = 20;
static const double IQDL_SUBPIX_EPS = 0.01;

// ============================================================================
// IQDL Result
// ============================================================================

// (IqdlResult is declared in dart_detect_internal.h)

// ============================================================================
// Helper: percentile clipping
// ============================================================================
static void clip_at_percentile(cv::Mat& img, double pct) {
    // Compute histogram to find percentile value
    int histSize = 256;
    float range[] = {0, 256};
    const float* histRange = {range};
    cv::Mat hist;
    cv::calcHist(&img, 1, 0, cv::Mat(), hist, 1, &histSize, &histRange);
    
    int total = img.rows * img.cols;
    int target = (int)(total * pct / 100.0);
    int cumsum = 0;
    int clip_val = 255;
    for (int i = 0; i < 256; i++) {
        cumsum += (int)hist.at<float>(i);
        if (cumsum >= target) {
            clip_val = i;
            break;
        }
    }
    
    if (clip_val > 0 && clip_val < 255) {
        img.convertTo(img, CV_8U, 255.0 / clip_val, 0);
    }
}

// ============================================================================
// Helper: find most elongated component
// ============================================================================
static cv::Mat find_elongated_component(const cv::Mat& binary) {
    cv::Mat labels, stats, centroids;
    int n = cv::connectedComponentsWithStats(binary, labels, stats, centroids);
    
    if (n <= 1) return cv::Mat();
    
    int best_idx = -1;
    double best_score = 0;
    
    for (int i = 1; i < n; i++) {
        int area = stats.at<int>(i, cv::CC_STAT_AREA);
        int w = stats.at<int>(i, cv::CC_STAT_WIDTH);
        int h = stats.at<int>(i, cv::CC_STAT_HEIGHT);
        if (area < IQDL_MIN_DART_AREA_PX) continue;
        
        double len = std::max(w, h);
        double wid = std::min(w, h) + 1.0;
        double elongation = len / wid;
        double score = elongation * area;  // prefer elongated AND large
        
        if (score > best_score) {
            best_score = score;
            best_idx = i;
        }
    }
    
    if (best_idx < 0) return cv::Mat();
    
    cv::Mat result = cv::Mat::zeros(binary.size(), CV_8U);
    result.setTo(255, labels == best_idx);
    return result;
}

// ============================================================================
// RANSAC line fit on edge points
// ============================================================================
struct RansacLine {
    double vx, vy, x0, y0;
    int inlier_count;
    double axis_length;
    std::vector<cv::Point2f> inliers;
    bool valid;
};

static RansacLine ransac_fit_line(const std::vector<cv::Point>& points, 
                                   double inlier_dist, int max_iters, int min_inliers) {
    RansacLine best;
    best.valid = false;
    best.inlier_count = 0;
    
    int N = (int)points.size();
    if (N < min_inliers) return best;
    
    // Simple deterministic seed for reproducibility
    unsigned int seed = 42;
    auto rng = [&seed]() -> int {
        seed = seed * 1103515245 + 12345;
        return (int)((seed >> 16) & 0x7FFF);
    };
    
    for (int iter = 0; iter < max_iters; iter++) {
        int i1 = rng() % N;
        int i2 = rng() % N;
        if (i1 == i2) continue;
        
        double dx = points[i2].x - points[i1].x;
        double dy = points[i2].y - points[i1].y;
        double len = std::sqrt(dx*dx + dy*dy);
        if (len < 3.0) continue;
        
        double nx = -dy / len;  // normal
        double ny = dx / len;
        
        // Count inliers
        std::vector<cv::Point2f> inliers;
        for (int k = 0; k < N; k++) {
            double dist = std::abs(nx * (points[k].x - points[i1].x) + 
                                   ny * (points[k].y - points[i1].y));
            if (dist <= inlier_dist) {
                inliers.push_back(cv::Point2f((float)points[k].x, (float)points[k].y));
            }
        }
        
        if ((int)inliers.size() > best.inlier_count) {
            best.inlier_count = (int)inliers.size();
            best.inliers = inliers;
            best.vx = dx / len;
            best.vy = dy / len;
            best.x0 = points[i1].x;
            best.y0 = points[i1].y;
        }
    }
    
    if (best.inlier_count < min_inliers) return best;
    
    // Refit line using all inliers (least squares)
    double mx = 0, my = 0;
    for (auto& p : best.inliers) { mx += p.x; my += p.y; }
    mx /= best.inliers.size();
    my /= best.inliers.size();
    
    double cxx = 0, cxy = 0, cyy = 0;
    for (auto& p : best.inliers) {
        double dx2 = p.x - mx;
        double dy2 = p.y - my;
        cxx += dx2 * dx2;
        cxy += dx2 * dy2;
        cyy += dy2 * dy2;
    }
    
    // Eigenvector of covariance for direction
    double trace = cxx + cyy;
    double det = cxx * cyy - cxy * cxy;
    double disc = std::sqrt(std::max(0.0, trace*trace/4.0 - det));
    double lam1 = trace/2.0 + disc;
    
    if (lam1 > 1e-6) {
        double evx = cxy;
        double evy = lam1 - cxx;
        double evlen = std::sqrt(evx*evx + evy*evy);
        if (evlen > 1e-6) {
            best.vx = evx / evlen;
            best.vy = evy / evlen;
        }
    }
    
    best.x0 = mx;
    best.y0 = my;
    
    // Compute axis length: project all inliers onto line direction
    double min_t = 1e9, max_t = -1e9;
    for (auto& p : best.inliers) {
        double t = (p.x - mx) * best.vx + (p.y - my) * best.vy;
        min_t = std::min(min_t, t);
        max_t = std::max(max_t, t);
    }
    best.axis_length = max_t - min_t;
    best.valid = (best.axis_length >= IQDL_MIN_AXIS_LENGTH_PX);
    
    return best;
}

// ============================================================================
// Subpixel tip refinement via gradient magnitude peak
// ============================================================================
static Point2f subpixel_tip_refine(const cv::Mat& diff_gray, Point2f tip_int, 
                                    double vx, double vy, int roi_size) {
    int half = roi_size / 2;
    int x0 = std::max(0, (int)tip_int.x - half);
    int y0 = std::max(0, (int)tip_int.y - half);
    int x1 = std::min(diff_gray.cols, (int)tip_int.x + half + 1);
    int y1 = std::min(diff_gray.rows, (int)tip_int.y + half + 1);
    
    if (x1 - x0 < 5 || y1 - y0 < 5) return tip_int;
    
    cv::Mat roi = diff_gray(cv::Rect(x0, y0, x1 - x0, y1 - y0));
    
    // Gradient magnitude in ROI
    cv::Mat gx, gy, gmag;
    cv::Sobel(roi, gx, CV_64F, 1, 0, 3);
    cv::Sobel(roi, gy, CV_64F, 0, 1, 3);
    cv::magnitude(gx, gy, gmag);
    
    // Weight by proximity to the line axis (perpendicular distance)
    // and by position along the axis (prefer the tip-ward end)
    cv::Mat weighted = cv::Mat::zeros(gmag.size(), CV_64F);
    double best_val = 0;
    double best_x = tip_int.x, best_y = tip_int.y;
    
    for (int r = 0; r < gmag.rows; r++) {
        for (int c = 0; c < gmag.cols; c++) {
            double px = c + x0;
            double py = r + y0;
            double dx = px - tip_int.x;
            double dy = py - tip_int.y;
            
            // Perpendicular distance to shaft axis
            double perp = std::abs(-vy * dx + vx * dy);
            double perp_weight = std::exp(-perp * perp / 4.0);
            
            // Along-axis distance (positive = further in tip direction)
            double along = vx * dx + vy * dy;
            double along_weight = std::exp(-along * along / (roi_size * roi_size / 4.0));
            
            double w = gmag.at<double>(r, c) * perp_weight * along_weight;
            if (w > best_val) {
                best_val = w;
                best_x = px;
                best_y = py;
            }
        }
    }
    
    // Refine around best point using 2D quadratic fit on 3x3 neighborhood
    int bx = (int)std::round(best_x) - x0;
    int by = (int)std::round(best_y) - y0;
    if (bx >= 1 && bx < gmag.cols - 1 && by >= 1 && by < gmag.rows - 1) {
        double v00 = gmag.at<double>(by-1, bx-1);
        double v10 = gmag.at<double>(by-1, bx);
        double v20 = gmag.at<double>(by-1, bx+1);
        double v01 = gmag.at<double>(by, bx-1);
        double v11 = gmag.at<double>(by, bx);
        double v21 = gmag.at<double>(by, bx+1);
        double v02 = gmag.at<double>(by+1, bx-1);
        double v12 = gmag.at<double>(by+1, bx);
        double v22 = gmag.at<double>(by+1, bx+1);
        
        double dx_num = (v21 - v01);
        double dx_den = 2.0 * (v01 - 2*v11 + v21);
        double dy_num = (v12 - v10);
        double dy_den = 2.0 * (v10 - 2*v11 + v12);
        
        double sub_dx = (std::abs(dx_den) > 1e-6) ? -dx_num / dx_den : 0.0;
        double sub_dy = (std::abs(dy_den) > 1e-6) ? -dy_num / dy_den : 0.0;
        
        sub_dx = std::max(-0.5, std::min(0.5, sub_dx));
        sub_dy = std::max(-0.5, std::min(0.5, sub_dy));
        
        best_x = bx + x0 + sub_dx;
        best_y = by + y0 + sub_dy;
    }
    
    return Point2f(best_x, best_y);
}

// ============================================================================
// Main IQDL function
// ============================================================================
IqdlResult run_iqdl(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    const cv::Mat& motion_mask,
    Point2f board_center,
    double resolution_scale,
    const cv::Mat& bbms_diff)
{
    IqdlResult res;
    res.valid = false;
    res.fallback = true;
    res.W_i = 0.0;
    
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
    
    // ---------------------------------------------------------------
    // Step 3: Dart-Only Differential Cleanup
    // ---------------------------------------------------------------
    cv::Mat diff;
    if (!bbms_diff.empty()) {
        diff = bbms_diff.clone();
    } else {
        cv::absdiff(gray_curr, gray_prev, diff);
    }
    
    // Gaussian blur
    int ksize = (int)(IQDL_GAUSS_BLUR_SIGMA * 6) | 1;
    if (ksize < 3) ksize = 3;
    cv::GaussianBlur(diff, diff, cv::Size(ksize, ksize), IQDL_GAUSS_BLUR_SIGMA);
    
    // Clip at percentile and normalize
    clip_at_percentile(diff, IQDL_DIFF_CLIP_PERCENTILE);
    
    // Apply motion mask to focus on dart region
    cv::Mat diff_masked;
    if (!motion_mask.empty()) {
        cv::bitwise_and(diff, motion_mask, diff_masked);
    } else {
        diff_masked = diff;
    }
    
    // Quality scoring for this frame
    cv::Mat laplacian;
    cv::Laplacian(diff_masked, laplacian, CV_64F);
    cv::Scalar mean_lap, std_lap;
    cv::meanStdDev(laplacian, mean_lap, std_lap);
    res.sharpness = std_lap[0] * std_lap[0];
    
    cv::Mat sobel_x, sobel_y, sobel_mag;
    cv::Sobel(diff_masked, sobel_x, CV_64F, 1, 0, 3);
    cv::Sobel(diff_masked, sobel_y, CV_64F, 0, 1, 3);
    cv::magnitude(sobel_x, sobel_y, sobel_mag);
    res.edge_energy = cv::sum(sobel_mag)[0];
    
    // Binary mask via Otsu
    cv::Mat binary;
    cv::threshold(diff_masked, binary, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    
    // Morphological cleanup
    cv::Mat open_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, 
        cv::Size(IQDL_MORPH_OPEN_K, IQDL_MORPH_OPEN_K));
    cv::Mat close_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, 
        cv::Size(IQDL_MORPH_CLOSE_K, IQDL_MORPH_CLOSE_K));
    cv::morphologyEx(binary, binary, cv::MORPH_OPEN, open_kern, cv::Point(-1,-1), 1);
    cv::morphologyEx(binary, binary, cv::MORPH_CLOSE, close_kern, cv::Point(-1,-1), 1);
    
    // Find most elongated component
    cv::Mat dart_mask = find_elongated_component(binary);
    if (dart_mask.empty()) {
        return res;  // fallback
    }
    
    res.dart_area = cv::countNonZero(dart_mask);
    
    // Count blobs
    cv::Mat lbl;
    res.blob_count = cv::connectedComponents(binary, lbl) - 1;
    
    // Quality score
    double Q = 0.35 * res.sharpness / 1000.0 + 0.35 * res.edge_energy / 100000.0 + 
               0.30 * res.dart_area / 500.0;
    if (res.blob_count > IQDL_MAX_BLOB_COUNT) Q *= 0.5;
    if (res.dart_area < IQDL_MIN_DART_AREA_PX) Q *= 0.5;
    res.Q = Q;
    
    // ---------------------------------------------------------------
    // Step 4: Shaft Axis Fit (RANSAC on Canny edges within dart mask)
    // ---------------------------------------------------------------
    cv::Mat edges;
    cv::Canny(diff_masked, edges, IQDL_CANNY_LOW, IQDL_CANNY_HIGH);
    cv::bitwise_and(edges, dart_mask, edges);
    
    std::vector<cv::Point> edge_pts;
    cv::findNonZero(edges, edge_pts);
    
    if ((int)edge_pts.size() < IQDL_MIN_INLIERS) {
        return res;  // fallback
    }
    
    RansacLine line = ransac_fit_line(edge_pts, IQDL_INLIER_DIST_PX, 
                                      IQDL_RANSAC_ITERS, IQDL_MIN_INLIERS);
    
    if (!line.valid) {
        return res;  // fallback
    }
    
    res.shaft_vx = line.vx;
    res.shaft_vy = line.vy;
    res.shaft_x0 = line.x0;
    res.shaft_y0 = line.y0;
    res.inlier_count = line.inlier_count;
    res.axis_length = line.axis_length;
    
    // ---------------------------------------------------------------
    // Step 5: Tip / Impact Point Estimation
    // ---------------------------------------------------------------
    // Find the two endpoints of the inlier set along the fitted axis
    double min_t = 1e9, max_t = -1e9;
    Point2f tip_fwd, tip_bwd;
    for (auto& p : line.inliers) {
        double t = (p.x - line.x0) * line.vx + (p.y - line.y0) * line.vy;
        if (t < min_t) { min_t = t; tip_bwd = Point2f(p.x, p.y); }
        if (t > max_t) { max_t = t; tip_fwd = Point2f(p.x, p.y); }
    }
    
    // Determine board-facing endpoint: closest to board center
    double dist_fwd = std::sqrt((tip_fwd.x - board_center.x) * (tip_fwd.x - board_center.x) +
                                 (tip_fwd.y - board_center.y) * (tip_fwd.y - board_center.y));
    double dist_bwd = std::sqrt((tip_bwd.x - board_center.x) * (tip_bwd.x - board_center.x) +
                                 (tip_bwd.y - board_center.y) * (tip_bwd.y - board_center.y));
    
    Point2f tip_int = (dist_fwd < dist_bwd) ? tip_fwd : tip_bwd;
    
    // Make line direction point toward board center
    double to_center_x = board_center.x - line.x0;
    double to_center_y = board_center.y - line.y0;
    if (line.vx * to_center_x + line.vy * to_center_y < 0) {
        res.shaft_vx = -line.vx;
        res.shaft_vy = -line.vy;
    }
    
    // Subpixel refinement
    Point2f tip_sub = subpixel_tip_refine(diff, tip_int, 
                                           res.shaft_vx, res.shaft_vy, IQDL_TIP_ROI_SIZE);
    
    res.tip_px = tip_int;
    res.tip_px_subpixel = tip_sub;
    
    // ---------------------------------------------------------------
    // Step 6: Per-Camera Confidence Weight
    // ---------------------------------------------------------------
    double total_edge = (int)edge_pts.size();
    double inlier_ratio = (total_edge > 0) ? line.inlier_count / total_edge : 0.0;
    
    // Expected axis length scales with resolution
    double expected_axis = 120.0 * resolution_scale;
    double axis_ratio = std::min(1.0, line.axis_length / expected_axis);
    
    double Q_norm = std::min(1.0, Q / 2.0);  // normalize Q to ~0-1
    
    res.W_i = std::max(0.0, std::min(1.0,
        0.35 * Q_norm + 
        0.35 * inlier_ratio + 
        0.30 * axis_ratio));
    
    res.valid = true;
    res.fallback = false;
    
    // Build PCA line for triangulation
    res.pca_line = PcaLine{res.shaft_vx, res.shaft_vy, res.shaft_x0, res.shaft_y0, 
                           line.axis_length / std::max(1.0, (double)IQDL_MIN_AXIS_LENGTH_PX), 
                           "iqdl_shaft"};
    
    return res;
}


// ============================================================================
// IQDL Refinement: Given an existing tip from legacy pipeline, 
// use IQDL's differential + shaft fit to refine the subpixel tip position.
// Only refines if IQDL result is consistent with legacy (shaft angle within threshold).
// ============================================================================
IqdlResult iqdl_refine_tip(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    const cv::Mat& motion_mask,
    Point2f board_center,
    Point2f legacy_tip,
    const std::optional<PcaLine>& legacy_line,
    double resolution_scale,
    const cv::Mat& bbms_diff)
{
    IqdlResult res = run_iqdl(current_frame, previous_frame, motion_mask, 
                               board_center, resolution_scale, bbms_diff);
    
    if (!res.valid || res.fallback) {
        // IQDL couldn't find a good shaft - return invalid, keep legacy
        res.valid = false;
        res.fallback = true;
        return res;
    }
    
    // Check shaft direction agreement with legacy barrel line
    if (legacy_line) {
        double dot = std::abs(res.shaft_vx * legacy_line->vx + res.shaft_vy * legacy_line->vy);
        double angle_deg = std::acos(std::min(1.0, dot)) * 180.0 / CV_PI;
        
        if (angle_deg > 15.0) {
            // IQDL shaft disagrees with legacy - don't use it
            res.valid = false;
            res.fallback = true;
            return res;
        }
    }
    
    // Check tip distance - IQDL tip shouldn't be too far from legacy tip
    double dx = res.tip_px_subpixel.x - legacy_tip.x;
    double dy = res.tip_px_subpixel.y - legacy_tip.y;
    double tip_dist = std::sqrt(dx*dx + dy*dy);
    double max_tip_dist = 20.0 * resolution_scale;  // ~20px at 1080p
    
    if (tip_dist > max_tip_dist) {
        // IQDL tip too far from legacy - don't use it
        res.valid = false;
        res.fallback = true;
        return res;
    }
    
    // Only use IQDL if confidence is sufficiently high
    if (res.W_i < 0.4) {
        res.valid = false;
        res.fallback = true;
        return res;
    }
    
    // IQDL agrees with legacy and has a close tip - use the blended result
    // Blend: weighted average favoring IQDL's subpixel precision
    double iqdl_weight = std::min(0.6, res.W_i);  // IQDL weight (max 0.6)
    double legacy_weight = 1.0 - iqdl_weight;
    
    res.tip_px_subpixel.x = iqdl_weight * res.tip_px_subpixel.x + legacy_weight * legacy_tip.x;
    res.tip_px_subpixel.y = iqdl_weight * res.tip_px_subpixel.y + legacy_weight * legacy_tip.y;
    
    res.valid = true;
    res.fallback = false;
    return res;
}

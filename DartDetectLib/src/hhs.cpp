/**
 * hhs.cpp - Phase 25: Hybrid Hypothesis Selection
 *
 * Generates multiple geometric candidates per dart and selects the best one
 * using a rule-based selector based on residuals, axis support, and camera quality.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <cmath>
#include <vector>
#include <map>
#include <string>
#include <numeric>
#include <opencv2/calib3d.hpp>

// ============================================================================
// Feature Flags (default OFF)
// ============================================================================
static bool g_use_hhs = false;
static bool g_hhs_enable_single = true;
static bool g_hhs_enable_pair = true;
static bool g_hhs_enable_tri = true;
static bool g_hhs_enable_rule_selector = true;
static bool g_hhs_fallback_to_dev4 = true;

// Parameters
static double g_hhs_R1 = 1.5;    // px threshold for tri inlier
static double g_hhs_R2 = 2.5;    // px threshold for single reproj
static double g_hhs_R3 = 2.0;    // px threshold for pair residual
static int    g_hhs_A1 = 40;     // axis inlier threshold
static double g_hhs_Q1 = 0.60;   // quality threshold for single

int set_hhs_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseHHS") { g_use_hhs = (value != 0); return 0; }
    if (s == "HHS_EnableSingleCameraCandidates") { g_hhs_enable_single = (value != 0); return 0; }
    if (s == "HHS_EnablePairCandidates") { g_hhs_enable_pair = (value != 0); return 0; }
    if (s == "HHS_EnableTriCandidate") { g_hhs_enable_tri = (value != 0); return 0; }
    if (s == "HHS_EnableRuleSelector") { g_hhs_enable_rule_selector = (value != 0); return 0; }
    if (s == "HHS_FallbackToDev4") { g_hhs_fallback_to_dev4 = (value != 0); return 0; }
    if (s == "HHS_R1") { g_hhs_R1 = value / 100.0; return 0; }
    if (s == "HHS_R2") { g_hhs_R2 = value / 100.0; return 0; }
    if (s == "HHS_R3") { g_hhs_R3 = value / 100.0; return 0; }
    if (s == "HHS_A1") { g_hhs_A1 = value; return 0; }
    if (s == "HHS_Q1") { g_hhs_Q1 = value / 100.0; return 0; }
    return -1;
}

bool is_hhs_enabled() { return g_use_hhs; }

// ============================================================================
// HHS Candidate Types
// ============================================================================

struct HhsCandidate {
    std::string type;        // "tri", "pair_XX_YY", "single_XX", "fallback"
    Point2f coords;          // normalized board coords
    double radius = 0;
    double theta_deg = 0;
    ScoreResult score;

    // Features
    double weighted_median_residual = 999.0;
    int inlier_camera_count = 0;
    std::map<std::string, double> reproj_error_per_cam;
    double radial_delta_from_tri = 0.0;
    int axis_support_count = 0;
    double sum_qi = 0.0;
    double max_qi = 0.0;
    int cameras_used = 0;
    double ring_boundary_distance = 999.0;
};

// Ring boundary radii
static const double HHS_RING_RADII[] = {
    6.35 / 170.0, 16.0 / 170.0, 99.0 / 170.0,
    107.0 / 170.0, 162.0 / 170.0, 1.0
};

static double ring_boundary_dist(double r) {
    double min_d = 999.0;
    for (int i = 0; i < 6; ++i)
        min_d = std::min(min_d, std::abs(r - HHS_RING_RADII[i]));
    return min_d;
}

// Compute perpendicular residual of point x to a warped line
static double perp_residual(const Point2f& x, const Point2f& line_pt,
                            double dir_x, double dir_y) {
    double nx = -dir_y, ny = dir_x;
    double dx = x.x - line_pt.x;
    double dy = x.y - line_pt.y;
    return std::abs(nx * dx + ny * dy);
}

// Score a point in normalized board space
static ScoreResult score_point(const Point2f& p) {
    double dist = std::sqrt(p.x * p.x + p.y * p.y);
    double angle_rad = std::atan2(p.y, -p.x);
    double angle_deg = angle_rad * 180.0 / CV_PI;
    if (angle_deg < 0) angle_deg += 360.0;
    angle_deg = std::fmod(angle_deg, 360.0);
    return score_from_polar(angle_deg, dist);
}

// Get wedge index for a point
static int get_wedge_index(const Point2f& p) {
    double angle_rad = std::atan2(p.y, -p.x);
    double angle_deg = angle_rad * 180.0 / CV_PI;
    if (angle_deg < 0) angle_deg += 360.0;
    angle_deg = std::fmod(angle_deg, 360.0);
    double adjusted = std::fmod(angle_deg - 90.0 + 9.0 + 360.0, 360.0);
    return ((int)(adjusted / 18.0)) % 20;
}

// ============================================================================
// Per-camera warped line data (recomputed from triangulation internals)
// ============================================================================
struct HhsCamData {
    Point2f warped_tip;       // tip in normalized space
    double warped_dir_x = 0;  // warped line direction
    double warped_dir_y = 0;
    double detection_quality = 0;
    double mask_quality = 0;
    double iqdl_Q = 0;
    int iqdl_inlier_count = 0;
    Point2f line_start, line_end;  // warped line endpoints
    bool valid = false;
};

// ============================================================================
// Main HHS Function
// ============================================================================

std::optional<IntersectionResult> hhs_select(
    const IntersectionResult& tri_result,
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations)
{
    if (!g_use_hhs) return std::nullopt;

    // --- Build per-camera warped data ---
    std::map<std::string, HhsCamData> cam_data;
    std::vector<std::string> cam_ids;

    for (const auto& [cam_id, det] : camera_results) {
        if (!det.pca_line || !det.tip) continue;
        auto cal_it = calibrations.find(cam_id);
        if (cal_it == calibrations.end()) continue;
        const TpsTransform& tps = cal_it->second.tps_cache;
        if (!tps.valid) continue;

        HhsCamData cd;
        cd.warped_tip = warp_point(tps, det.tip->x, det.tip->y);
        cd.mask_quality = det.mask_quality;
        cd.iqdl_Q = det.hhs_iqdl_Q;
        cd.iqdl_inlier_count = det.hhs_iqdl_inlier_count;

        // Compute warped direction via homography multi-sample fitLine
        std::vector<cv::Point2f> sv, dv;
        for (int i = 0; i < tps.src_points.rows; ++i) {
            sv.push_back(cv::Point2f((float)tps.src_points.at<double>(i, 0),
                                      (float)tps.src_points.at<double>(i, 1)));
            dv.push_back(cv::Point2f((float)tps.dst_points.at<double>(i, 0),
                                      (float)tps.dst_points.at<double>(i, 1)));
        }
        cv::Mat H = cv::findHomography(sv, dv, cv::RANSAC, 5.0);

        double vx = det.pca_line->vx, vy = det.pca_line->vy;
        double extent = 200.0;
        const int NS = 21;
        std::vector<cv::Point2f> src_pts, warped_pts;
        for (int t = 0; t < NS; ++t) {
            double frac = (double)t / (NS - 1);
            double db = extent * (1.0 - frac);
            src_pts.push_back(cv::Point2f(
                (float)(det.tip->x - vx * db),
                (float)(det.tip->y - vy * db)));
        }
        if (!H.empty()) {
            cv::perspectiveTransform(src_pts, warped_pts, H);
        } else {
            for (auto& sp : src_pts) {
                Point2f wp = warp_point(tps, sp.x, sp.y);
                warped_pts.push_back(cv::Point2f(wp.x, wp.y));
            }
        }

        cv::Vec4f fit;
        cv::fitLine(warped_pts, fit, cv::DIST_HUBER, 0, 0.01, 0.01);
        double wvx = fit[0], wvy = fit[1];
        double len = std::sqrt(wvx * wvx + wvy * wvy);
        cd.warped_dir_x = (len > 1e-12) ? wvx / len : 0;
        cd.warped_dir_y = (len > 1e-12) ? wvy / len : 0;

        cd.line_start = Point2f(cd.warped_tip.x - cd.warped_dir_x * 2.0,
                                cd.warped_tip.y - cd.warped_dir_y * 2.0);
        cd.line_end = cd.warped_tip;

        // Detection quality
        double dq_inlier = std::max(0.3, std::min(1.0, det.ransac_inlier_ratio));
        double dq_pixels = std::min(1.0, det.barrel_pixel_count / 200.0);
        double dq_aspect = std::min(1.0, det.barrel_aspect_ratio / 8.0);
        cd.detection_quality = std::max(0.1, 0.5 * dq_inlier + 0.3 * dq_pixels + 0.2 * dq_aspect);
        if (det.barrel_pixel_count == 0) cd.detection_quality *= 0.5;

        cd.valid = true;
        cam_data[cam_id] = cd;
        cam_ids.push_back(cam_id);
    }

    if (cam_ids.size() < 2) return std::nullopt;
    std::sort(cam_ids.begin(), cam_ids.end());

    // Baseline = tri result
    Point2f tri_coords = tri_result.coords;
    int baseline_wedge = get_wedge_index(tri_coords);

    // --- Generate Candidates ---
    std::vector<HhsCandidate> candidates;

    // H_tri: existing BCWT/triangulation result
    if (g_hhs_enable_tri && tri_result.segment > 0) {
        HhsCandidate h;
        h.type = "tri";
        h.coords = tri_coords;
        h.radius = std::sqrt(h.coords.x * h.coords.x + h.coords.y * h.coords.y);
        h.score = score_point(h.coords);
        h.cameras_used = (int)cam_ids.size();
        candidates.push_back(h);
    }

    // H_pair: pairwise intersections
    if (g_hhs_enable_pair) {
        for (size_t i = 0; i < cam_ids.size(); ++i) {
            for (size_t j = i + 1; j < cam_ids.size(); ++j) {
                const auto& c1 = cam_data[cam_ids[i]];
                const auto& c2 = cam_data[cam_ids[j]];
                if (!c1.valid || !c2.valid) continue;

                auto ix = intersect_lines_2d(
                    c1.line_start.x, c1.line_start.y, c1.line_end.x, c1.line_end.y,
                    c2.line_start.x, c2.line_start.y, c2.line_end.x, c2.line_end.y);
                if (!ix) continue;

                double d = std::sqrt(ix->x * ix->x + ix->y * ix->y);
                if (d > 1.3) continue;

                HhsCandidate h;
                h.type = "pair_" + cam_ids[i] + "_" + cam_ids[j];
                h.coords = *ix;
                h.radius = d;
                h.score = score_point(h.coords);
                h.cameras_used = 2;
                candidates.push_back(h);
            }
        }
    }

    // H_single: per-camera shaft axis projection
    if (g_hhs_enable_single) {
        for (const auto& cid : cam_ids) {
            const auto& cd = cam_data[cid];
            if (!cd.valid) continue;

            // Project tip along shaft axis to find intersection with board
            // Use the warped tip directly as the single-camera estimate
            double d = std::sqrt(cd.warped_tip.x * cd.warped_tip.x +
                                 cd.warped_tip.y * cd.warped_tip.y);
            if (d > 1.3 || d < 0.01) continue;

            HhsCandidate h;
            h.type = "single_" + cid;
            h.coords = cd.warped_tip;
            h.radius = d;
            h.score = score_point(h.coords);
            h.cameras_used = 1;
            h.max_qi = cd.iqdl_Q;
            h.sum_qi = cd.iqdl_Q;
            h.axis_support_count = cd.iqdl_inlier_count;
            candidates.push_back(h);
        }
    }

    if (candidates.empty()) return std::nullopt;

    // --- Compute per-candidate features ---
    for (auto& h : candidates) {
        std::vector<double> residuals;
        double sq = 0, mq = 0;
        int inliers = 0;

        for (const auto& cid : cam_ids) {
            const auto& cd = cam_data[cid];
            if (!cd.valid) continue;

            double res = perp_residual(h.coords, cd.line_end,
                                       cd.warped_dir_x, cd.warped_dir_y);
            residuals.push_back(res);
            h.reproj_error_per_cam[cid] = res;

            // Inlier threshold in normalized units (~1.5px mapped to ~0.01 norm)
            if (res < g_hhs_R1 * 0.01) inliers++;

            sq += cd.iqdl_Q;
            mq = std::max(mq, cd.iqdl_Q);

            // Axis support: count cameras whose shaft aligns with candidate
            if (cd.iqdl_inlier_count > 0) {
                // Check if line direction points roughly toward candidate
                double dx = h.coords.x - cd.warped_tip.x;
                double dy = h.coords.y - cd.warped_tip.y;
                double dot = dx * cd.warped_dir_x + dy * cd.warped_dir_y;
                if (dot > -0.05) {  // roughly same half-plane
                    h.axis_support_count += cd.iqdl_inlier_count;
                }
            }
        }

        if (!residuals.empty()) {
            std::sort(residuals.begin(), residuals.end());
            h.weighted_median_residual = residuals[residuals.size() / 2];
        }
        h.inlier_camera_count = inliers;
        h.sum_qi = sq;
        h.max_qi = mq;
        h.radial_delta_from_tri = std::abs(h.radius -
            std::sqrt(tri_coords.x * tri_coords.x + tri_coords.y * tri_coords.y));
        h.ring_boundary_distance = ring_boundary_dist(h.radius);
        h.theta_deg = std::atan2(h.coords.y, -h.coords.x) * 180.0 / CV_PI;
        if (h.theta_deg < 0) h.theta_deg += 360.0;
    }

    // --- Rule-Based Selector ---
    if (!g_hhs_enable_rule_selector) return std::nullopt;

    const HhsCandidate* selected = nullptr;
    std::string selection_reason;

    // Priority 1: H_tri with >=2 inlier cameras and low residual
    for (const auto& h : candidates) {
        if (h.type == "tri" && h.inlier_camera_count >= 2 &&
            h.weighted_median_residual <= g_hhs_R1 * 0.01) {
            selected = &h;
            selection_reason = "tri_high_conf";
            break;
        }
    }

    // Priority 2: Best H_single with axis support + quality
    if (!selected) {
        const HhsCandidate* best_single = nullptr;
        double best_single_q = -1;
        for (const auto& h : candidates) {
            if (h.type.substr(0, 7) != "single_") continue;
            if (h.axis_support_count >= g_hhs_A1 && h.max_qi >= g_hhs_Q1) {
                // Check reproj on other cameras
                double max_other_reproj = 0;
                for (const auto& [cid, res] : h.reproj_error_per_cam) {
                    if (h.type != "single_" + cid)
                        max_other_reproj = std::max(max_other_reproj, res);
                }
                if (max_other_reproj <= g_hhs_R2 * 0.01 && h.max_qi > best_single_q) {
                    best_single = &h;
                    best_single_q = h.max_qi;
                }
            }
        }
        if (best_single) {
            selected = best_single;
            selection_reason = "single_axis_quality";
        }
    }

    // Priority 3: Best H_pair with low residual
    if (!selected) {
        const HhsCandidate* best_pair = nullptr;
        double best_pair_res = 999;
        for (const auto& h : candidates) {
            if (h.type.substr(0, 5) != "pair_") continue;
            if (h.weighted_median_residual <= g_hhs_R3 * 0.01 &&
                h.weighted_median_residual < best_pair_res) {
                best_pair = &h;
                best_pair_res = h.weighted_median_residual;
            }
        }
        if (best_pair) {
            selected = best_pair;
            selection_reason = "pair_low_residual";
        }
    }

    // Priority 4: Fallback
    if (!selected) {
        if (g_hhs_fallback_to_dev4) {
            // Return nullopt to keep the original tri_result
            return std::nullopt;
        }
        // Use tri if available
        for (const auto& h : candidates) {
            if (h.type == "tri") { selected = &h; selection_reason = "fallback_tri"; break; }
        }
        if (!selected) return std::nullopt;
    }

    // --- Wedge Guard: only allow +-1 step from baseline ---
    int sel_wedge = get_wedge_index(selected->coords);
    int wedge_diff = std::abs(sel_wedge - baseline_wedge);
    if (wedge_diff > 10) wedge_diff = 20 - wedge_diff;  // wraparound
    if (wedge_diff > 1) {
        // Too far from baseline, reject override
        return std::nullopt;
    }

    // If selected is same as tri, no override needed
    if (selected->type == "tri") return std::nullopt;

    // --- Build override result ---
    IntersectionResult result = tri_result;  // copy baseline
    result.coords = selected->coords;
    result.segment = selected->score.segment;
    result.multiplier = selected->score.multiplier;
    result.score = selected->score.score;
    result.method = "HHS_" + selected->type;

    // Add HHS debug to tri_debug
    if (result.tri_debug) {
        auto& td = *result.tri_debug;
        td.hhs_applied = true;
        td.hhs_selected_type = selected->type;
        td.hhs_selection_reason = selection_reason;
        td.hhs_candidate_count = (int)candidates.size();
        td.hhs_baseline_wedge = baseline_wedge;
        td.hhs_selected_wedge = sel_wedge;
        td.hhs_selected_residual = selected->weighted_median_residual;
        td.hhs_selected_axis_support = selected->axis_support_count;
        td.hhs_selected_qi = selected->max_qi;
    }

    return result;
}


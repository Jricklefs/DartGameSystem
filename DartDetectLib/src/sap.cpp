/**
 * sap.cpp - Phase 19B: Soft Accept Prevention (SAP)
 *
 * Intercepts MISS decisions BEFORE they are finalized by attempting a
 * relaxed triangulation using cameras that meet lower quality thresholds.
 * If the relaxed result passes geometric validation gates, the dart is
 * accepted as a "SoftAccept" instead of MISS.
 *
 * Does NOT modify: calibration, warp math, segmentation, BCWT internals,
 * Phase 10B radial clamp, or IQDL detection logic.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <cmath>
#include <numeric>
#include <vector>
#include <set>
#include <opencv2/calib3d.hpp>

// ============================================================================
// Feature Flags (default OFF / sub-flags default ON)
// ============================================================================
static bool g_use_sap = false;
static bool g_sap_relaxed_triangulation = true;
static bool g_sap_weak_cam_inclusion = true;
static bool g_sap_board_containment_gate = true;

// ============================================================================
// Relaxed Thresholds
// ============================================================================
static constexpr double SAP_MIN_Q_RELAXED = 0.40;
static constexpr int    SAP_MIN_AXIS_INLIERS_RELAXED = 30;
static constexpr double SAP_MIN_AXIS_LENGTH_PX_RELAXED = 22.0;
static constexpr double SAP_MAX_THETA_SPREAD_RELAXED_DEG = 8.0;
static constexpr double SAP_MAX_RESIDUAL_RATIO_RELAXED = 1.40;
static constexpr int    SAP_MIN_CAMERAS_RELAXED = 2;
static constexpr double SAP_BOARD_OUTER_RADIUS = 1.0;
static constexpr double SAP_HISTORICAL_MEDIAN_RESIDUAL = 0.04;

// ============================================================================
// Flag setter
// ============================================================================
int set_sap_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseSoftAcceptPrevention") { g_use_sap = (value != 0); return 0; }
    if (s == "SAP_EnableRelaxedTriangulation") { g_sap_relaxed_triangulation = (value != 0); return 0; }
    if (s == "SAP_EnableWeakCamInclusion") { g_sap_weak_cam_inclusion = (value != 0); return 0; }
    if (s == "SAP_EnableBoardContainmentGate") { g_sap_board_containment_gate = (value != 0); return 0; }
    return -1;
}

// ============================================================================
// Helpers
// ============================================================================
static double circular_arc_spread_sap(const std::vector<double>& angles_deg) {
    if (angles_deg.size() < 2) return 0.0;
    std::vector<double> sorted = angles_deg;
    for (auto& a : sorted) {
        while (a < 0) a += 360.0;
        while (a >= 360.0) a -= 360.0;
    }
    std::sort(sorted.begin(), sorted.end());
    double max_gap = sorted[0] + 360.0 - sorted.back();
    for (size_t i = 1; i < sorted.size(); ++i) {
        double gap = sorted[i] - sorted[i - 1];
        if (gap > max_gap) max_gap = gap;
    }
    return 360.0 - max_gap;
}

// ============================================================================
// Main SAP pipeline
// ============================================================================
SapResult run_sap(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations,
    const std::map<std::string, IqdlResult>& iqdl_results,
    const IntersectionResult* baseline_result)
{
    SapResult sap;
    sap.baseline_would_miss = true;

    if (!g_use_sap || !g_sap_relaxed_triangulation) return sap;

    // STEP 2: Identify relaxed candidate cameras from IQDL evidence
    struct RelaxedCam {
        std::string cam_id;
        double Q;
        int inlier_count;
        double axis_length;
        double theta_deg;
    };
    std::vector<RelaxedCam> relaxed_cams;

    for (const auto& [cam_id, iqdl] : iqdl_results) {
        if (!iqdl.valid) continue;

        bool passes = iqdl.Q >= SAP_MIN_Q_RELAXED
                   && iqdl.inlier_count >= SAP_MIN_AXIS_INLIERS_RELAXED
                   && iqdl.axis_length >= SAP_MIN_AXIS_LENGTH_PX_RELAXED;

        if (g_sap_weak_cam_inclusion && !passes) {
            // Allow if Q is decent but inliers slightly low
            if (iqdl.Q >= SAP_MIN_Q_RELAXED && iqdl.axis_length >= SAP_MIN_AXIS_LENGTH_PX_RELAXED)
                passes = true;
        }

        if (passes) {
            auto cal_it = calibrations.find(cam_id);
            auto det_it = camera_results.find(cam_id);
            if (cal_it == calibrations.end() || det_it == camera_results.end()) continue;
            if (!det_it->second.tip || !det_it->second.pca_line) continue;

            const auto& tps = cal_it->second.tps_cache;
            if (!tps.valid) continue;

            const auto& det = det_it->second;
            Point2f tip_n = warp_point(tps, det.tip->x, det.tip->y);
            double vx = det.pca_line->vx, vy = det.pca_line->vy;
            double extent = 200.0;
            Point2f back_px(det.tip->x - vx * extent, det.tip->y - vy * extent);
            Point2f back_n = warp_point(tps, back_px.x, back_px.y);
            double dx = tip_n.x - back_n.x;
            double dy = tip_n.y - back_n.y;
            double theta = std::atan2(dy, dx) * 180.0 / CV_PI;

            RelaxedCam rc;
            rc.cam_id = cam_id;
            rc.Q = iqdl.Q;
            rc.inlier_count = iqdl.inlier_count;
            rc.axis_length = iqdl.axis_length;
            rc.theta_deg = theta;
            relaxed_cams.push_back(rc);
        }
    }

    sap.relaxed_cam_count = (int)relaxed_cams.size();
    for (const auto& rc : relaxed_cams)
        sap.relaxed_cam_ids += (sap.relaxed_cam_ids.empty() ? "" : ",") + rc.cam_id;

    if ((int)relaxed_cams.size() < SAP_MIN_CAMERAS_RELAXED) return sap;

    // Check angular agreement
    std::vector<double> thetas;
    for (const auto& rc : relaxed_cams) thetas.push_back(rc.theta_deg);
    double theta_spread = circular_arc_spread_sap(thetas);
    sap.theta_spread_relaxed = theta_spread;

    if (theta_spread > SAP_MAX_THETA_SPREAD_RELAXED_DEG) {
        sap.angular_gate_pass = false;
        return sap;
    }
    sap.angular_gate_pass = true;

    // STEP 3: Compute relaxed triangulation using warped lines
    struct WarpedLine {
        Point2f start, end;
        std::string cam_id;
    };
    std::vector<WarpedLine> warped_lines;

    for (const auto& rc : relaxed_cams) {
        auto cal_it = calibrations.find(rc.cam_id);
        auto det_it = camera_results.find(rc.cam_id);
        if (cal_it == calibrations.end() || det_it == camera_results.end()) continue;
        const auto& det = det_it->second;
        if (!det.tip || !det.pca_line) continue;
        const auto& tps = cal_it->second.tps_cache;
        if (!tps.valid) continue;

        double vx = det.pca_line->vx, vy = det.pca_line->vy;
        double extent = 200.0;
        const int N_SAMPLES = 21;

        // Use homography approach (same as triangulation.cpp)
        std::vector<cv::Point2f> sv, dv;
        for (int hi = 0; hi < tps.src_points.rows; ++hi) {
            sv.push_back(cv::Point2f((float)tps.src_points.at<double>(hi, 0),
                                      (float)tps.src_points.at<double>(hi, 1)));
            dv.push_back(cv::Point2f((float)tps.dst_points.at<double>(hi, 0),
                                      (float)tps.dst_points.at<double>(hi, 1)));
        }
        cv::Mat H_mat = cv::findHomography(sv, dv, cv::RANSAC, 5.0);

        std::vector<cv::Point2f> warped_pts;
        if (!H_mat.empty()) {
            std::vector<cv::Point2f> src_pts_h;
            for (int t = 0; t < N_SAMPLES; ++t) {
                double frac = (double)t / (N_SAMPLES - 1);
                double dist_back = extent * (1.0 - frac);
                src_pts_h.push_back(cv::Point2f(
                    (float)(det.tip->x - vx * dist_back),
                    (float)(det.tip->y - vy * dist_back)));
            }
            cv::perspectiveTransform(src_pts_h, warped_pts, H_mat);
        } else {
            for (int t = 0; t < N_SAMPLES; ++t) {
                double frac = (double)t / (N_SAMPLES - 1);
                double dist_back = extent * (1.0 - frac);
                double px = det.tip->x - vx * dist_back;
                double py = det.tip->y - vy * dist_back;
                Point2f wp = warp_point(tps, px, py);
                warped_pts.push_back(cv::Point2f(wp.x, wp.y));
            }
        }

        cv::Vec4f warped_line_fit;
        cv::fitLine(warped_pts, warped_line_fit, cv::DIST_HUBER, 0, 0.01, 0.01);
        double wvx = warped_line_fit[0], wvy = warped_line_fit[1];
        Point2f tip_n = warp_point(tps, det.tip->x, det.tip->y);

        WarpedLine wl;
        wl.start = Point2f(tip_n.x - wvx * 2.0, tip_n.y - wvy * 2.0);
        wl.end = tip_n;
        wl.cam_id = rc.cam_id;
        warped_lines.push_back(wl);
    }

    if (warped_lines.size() < 2) return sap;

    // Find best pairwise intersection
    Point2f best_ix;
    double best_err = 1e9;
    bool found_ix = false;

    for (size_t i = 0; i < warped_lines.size(); ++i) {
        for (size_t j = i + 1; j < warped_lines.size(); ++j) {
            auto ix = intersect_lines_2d(
                warped_lines[i].start.x, warped_lines[i].start.y,
                warped_lines[i].end.x, warped_lines[i].end.y,
                warped_lines[j].start.x, warped_lines[j].start.y,
                warped_lines[j].end.x, warped_lines[j].end.y);
            if (!ix) continue;
            double e1 = std::hypot(ix->x - warped_lines[i].end.x, ix->y - warped_lines[i].end.y);
            double e2 = std::hypot(ix->x - warped_lines[j].end.x, ix->y - warped_lines[j].end.y);
            double err = e1 + e2;
            if (err < best_err) {
                best_err = err;
                best_ix = *ix;
                found_ix = true;
            }
        }
    }

    if (!found_ix) return sap;

    // STEP 4: Geometric validation gates
    double radius_soft = std::hypot(best_ix.x, best_ix.y);
    sap.residual_soft = best_err;

    // Gate 1: Board containment
    if (g_sap_board_containment_gate && radius_soft > SAP_BOARD_OUTER_RADIUS) {
        sap.board_containment_pass = false;
        return sap;
    }
    sap.board_containment_pass = true;

    // Gate 3: Residual sanity
    double median_ref = SAP_HISTORICAL_MEDIAN_RESIDUAL;
    if (baseline_result && baseline_result->tri_debug) {
        double mr = baseline_result->tri_debug->median_residual;
        if (mr > 0.001) median_ref = mr;
    }
    if (best_err > median_ref * SAP_MAX_RESIDUAL_RATIO_RELAXED) {
        sap.residual_gate_pass = false;
        return sap;
    }
    sap.residual_gate_pass = true;

    // STEP 5: Accept soft result
    double final_angle_rad = std::atan2(best_ix.y, -best_ix.x);
    double final_angle_deg = final_angle_rad * 180.0 / CV_PI;
    if (final_angle_deg < 0) final_angle_deg += 360.0;
    final_angle_deg = std::fmod(final_angle_deg, 360.0);
    ScoreResult final_score = score_from_polar(final_angle_deg, radius_soft);

    // Conservative confidence = min Q among relaxed cams
    double min_q = 1.0;
    for (const auto& rc : relaxed_cams)
        min_q = std::min(min_q, rc.Q);

    // Build override result
    IntersectionResult override_res;
    override_res.segment = final_score.segment;
    override_res.multiplier = final_score.multiplier;
    override_res.score = final_score.score;
    override_res.method = "SoftAccept_RelaxedTriangulation";
    override_res.confidence = min_q;
    override_res.coords = best_ix;
    override_res.total_error = best_err;

    if (baseline_result) {
        override_res.per_camera = baseline_result->per_camera;
    }

    IntersectionResult::TriangulationDebug td;
    td.board_radius = radius_soft;
    td.median_residual = best_err;
    td.angle_spread_deg = theta_spread;
    td.final_confidence = min_q;
    override_res.tri_debug = td;

    sap.soft_accept_applied = true;
    sap.final_segment = final_score.segment;
    sap.final_multiplier = final_score.multiplier;
    sap.final_score = final_score.score;
    sap.override_result = override_res;

    return sap;
}

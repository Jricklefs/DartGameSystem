/**
 * mfr.cpp - Phase 19: Miss False-Negative Recovery
 *
 * When the baseline pipeline returns a MISS (segment==0), this module
 * evaluates IQDL per-camera evidence to identify "strong" cameras and
 * attempts a recovery triangulation using only those cameras.
 *
 * Does NOT modify: calibration, warp math, wedge/ring segmentation,
 * motion mask, Phase 10B radial clamp, or BCWT internals.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <cmath>
#include <numeric>
#include <vector>
#include <set>
#include <iostream>

// ============================================================================
// Feature Flags (default OFF / sub-flags default ON)
// ============================================================================
static bool g_use_mfr = false;
static bool g_mfr_axis_evidence_gate = true;
static bool g_mfr_two_camera_override = true;
static bool g_mfr_conservative_ring_guard = true;
static bool g_mfr_fallback_to_baseline = true;

// ============================================================================
// Parameters
// ============================================================================
static constexpr double MFR_EPS = 1e-6;
static constexpr double MFR_MIN_Q = 0.55;
static constexpr int    MFR_MIN_AXIS_INLIERS = 45;
static constexpr double MFR_MIN_AXIS_LENGTH_PX = 28.0;
static constexpr double MFR_MAX_REPROJ_ERR_PX = 2.5;
static constexpr int    MFR_MIN_CAMERAS_STRONG = 2;
static constexpr double MFR_MAX_THETA_SPREAD_DEG = 6.0;
static constexpr double MFR_MAX_RESIDUAL_RATIO = 1.15;
static constexpr double MFR_MAX_RADIUS_SHIFT_FRAC = 0.012;  // * board_radius(=1.0)
static constexpr double MFR_RING_GUARD_MARGIN = 0.006;  // ~1mm in normalized units (1/170)

// Ring boundary radii (normalized, same as triangulation.cpp)
static const double MFR_RING_RADII[] = {
    6.35 / 170.0,
    16.0 / 170.0,
    99.0 / 170.0,
    107.0 / 170.0,
    162.0 / 170.0,
    170.0 / 170.0,
};
static const int MFR_NUM_RINGS = 6;

static double min_ring_boundary_distance(double r) {
    double min_d = 1e9;
    for (int i = 0; i < MFR_NUM_RINGS; ++i) {
        double d = std::abs(r - MFR_RING_RADII[i]);
        if (d < min_d) min_d = d;
    }
    return min_d;
}

// Compute minimal circular arc spread for a set of angles (degrees)
static double circular_arc_spread(const std::vector<double>& angles_deg) {
    if (angles_deg.size() < 2) return 0.0;
    std::vector<double> sorted = angles_deg;
    for (auto& a : sorted) {
        while (a < 0) a += 360.0;
        while (a >= 360.0) a -= 360.0;
    }
    std::sort(sorted.begin(), sorted.end());
    
    // Minimal arc = 360 - max gap
    double max_gap = sorted[0] + 360.0 - sorted.back();
    for (size_t i = 1; i < sorted.size(); ++i) {
        double gap = sorted[i] - sorted[i-1];
        if (gap > max_gap) max_gap = gap;
    }
    return 360.0 - max_gap;
}

// ============================================================================
// Flag setter (called from dd_set_flag chain)
// ============================================================================
int set_mfr_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseMFR") { g_use_mfr = (value != 0); return 0; }
    if (s == "MFR_EnableAxisEvidenceGate") { g_mfr_axis_evidence_gate = (value != 0); return 0; }
    if (s == "MFR_EnableTwoCameraOverride") { g_mfr_two_camera_override = (value != 0); return 0; }
    if (s == "MFR_EnableConservativeRingGuard") { g_mfr_conservative_ring_guard = (value != 0); return 0; }
    if (s == "MFR_FallbackToBaselineMissLogic") { g_mfr_fallback_to_baseline = (value != 0); return 0; }
    return -1;
}

// ============================================================================
// Main MFR pipeline
// ============================================================================
MfrResult run_mfr(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations,
    const std::map<std::string, IqdlResult>& iqdl_results,
    const IntersectionResult* baseline_result)
{
    MfrResult mfr;
    mfr.baseline_is_miss = true;

    // If MFR disabled, return immediately
    if (!g_use_mfr) {
        mfr.miss_override_reason = "MFR_Disabled";
        return mfr;
    }

    // ---------------------------------------------------------------
    // STEP 1: Select Strong Cameras using IQDL evidence
    // ---------------------------------------------------------------
    std::vector<MfrCameraEvidence> all_evidence;
    std::vector<std::string> strong_cam_ids;

    for (const auto& [cam_id, iqdl] : iqdl_results) {
        MfrCameraEvidence ev;
        ev.cam_id = cam_id;
        ev.Q = iqdl.Q;
        ev.axis_inliers = iqdl.inlier_count;
        ev.axis_length_px = iqdl.axis_length;
        ev.fallback_used = iqdl.fallback;
        ev.reprojection_error = -1.0;  // not available in current IQDL struct

        // Compute theta from warped tip position
        auto cal_it = calibrations.find(cam_id);
        if (cal_it != calibrations.end() && cal_it->second.tps_cache.valid && iqdl.valid) {
            Point2f tip_n = warp_point(cal_it->second.tps_cache,
                                       iqdl.tip_px_subpixel.x,
                                       iqdl.tip_px_subpixel.y);
            double theta_rad = std::atan2(tip_n.y, -tip_n.x);
            ev.theta_deg = theta_rad * 180.0 / CV_PI;
            if (ev.theta_deg < 0) ev.theta_deg += 360.0;
        }

        // Strong camera criteria
        ev.is_strong = (
            ev.Q >= MFR_MIN_Q &&
            ev.axis_inliers >= MFR_MIN_AXIS_INLIERS &&
            ev.axis_length_px >= MFR_MIN_AXIS_LENGTH_PX &&
            !ev.fallback_used
        );
        if (ev.reprojection_error >= 0 && ev.reprojection_error > MFR_MAX_REPROJ_ERR_PX) {
            ev.is_strong = false;
        }

        all_evidence.push_back(ev);
        if (ev.is_strong) {
            strong_cam_ids.push_back(cam_id);
        }
    }

    mfr.strong_cameras_count = (int)strong_cam_ids.size();
    {
        std::string ids;
        for (size_t i = 0; i < strong_cam_ids.size(); ++i) {
            if (i > 0) ids += ",";
            ids += strong_cam_ids[i];
        }
        mfr.strong_camera_ids = ids;
    }

    if (mfr.strong_cameras_count < MFR_MIN_CAMERAS_STRONG) {
        mfr.miss_override_reason = "MISS_MFR_NoOverride_InsufficientStrongCams";
        return mfr;
    }

    // ---------------------------------------------------------------
    // STEP 2: Check angular agreement among strong cameras
    // ---------------------------------------------------------------
    std::vector<double> strong_thetas;
    for (const auto& ev : all_evidence) {
        if (ev.is_strong) {
            strong_thetas.push_back(ev.theta_deg);
        }
    }
    mfr.theta_spread_deg_strong = circular_arc_spread(strong_thetas);

    if (mfr.theta_spread_deg_strong > MFR_MAX_THETA_SPREAD_DEG) {
        mfr.miss_override_reason = "MISS_MFR_NoOverride_StrongCamDisagreement";
        return mfr;
    }

    // ---------------------------------------------------------------
    // STEP 3: Compute override point using strong cameras only
    // ---------------------------------------------------------------
    // Build subset of camera_results with only strong cameras
    std::map<std::string, DetectionResult> strong_camera_results;
    std::map<std::string, CameraCalibration> strong_calibrations;
    for (const auto& cam_id : strong_cam_ids) {
        auto det_it = camera_results.find(cam_id);
        auto cal_it = calibrations.find(cam_id);
        if (det_it != camera_results.end() && cal_it != calibrations.end()) {
            strong_camera_results[cam_id] = det_it->second;
            strong_calibrations[cam_id] = cal_it->second;
        }
    }

    if (strong_camera_results.size() < 2) {
        mfr.miss_override_reason = "MISS_MFR_NoOverride_InsufficientStrongCams";
        return mfr;
    }

    // Re-run triangulation with only strong cameras
    auto tri_override = triangulate_with_line_intersection(strong_camera_results, strong_calibrations);
    if (!tri_override || tri_override->segment == 0) {
        mfr.miss_override_reason = "MISS_MFR_NoOverride_TriangulationFailed";
        return mfr;
    }

    mfr.x_mfr_x = tri_override->coords.x;
    mfr.x_mfr_y = tri_override->coords.y;
    // After Phase 10B radial clamp (already applied inside triangulate_with_line_intersection)
    mfr.x_mfr_clamped_x = tri_override->coords.x;
    mfr.x_mfr_clamped_y = tri_override->coords.y;

    double radius_mfr = std::sqrt(mfr.x_mfr_clamped_x * mfr.x_mfr_clamped_x +
                                   mfr.x_mfr_clamped_y * mfr.x_mfr_clamped_y);
    mfr.residual_mfr = tri_override->total_error;

    // ---------------------------------------------------------------
    // STEP 4: Conservative Ring Guard
    // ---------------------------------------------------------------
    if (g_mfr_conservative_ring_guard) {
        double ring_dist = min_ring_boundary_distance(radius_mfr);
        mfr.ring_boundary_distance = ring_dist;
        if (ring_dist < MFR_RING_GUARD_MARGIN) {
            mfr.miss_override_reason = "MISS_MFR_NoOverride_RingGuard";
            return mfr;
        }
    }

    // ---------------------------------------------------------------
    // STEP 5: Residual / Stability Gate
    // ---------------------------------------------------------------
    double baseline_residual = MFR_EPS;
    if (baseline_result && baseline_result->total_error > MFR_EPS) {
        baseline_residual = baseline_result->total_error;
    } else {
        // Use median of strong cam evidence as reference
        // Just use the MFR residual itself as a pass-through (no baseline to compare)
        baseline_residual = mfr.residual_mfr;
    }

    mfr.residual_ratio = mfr.residual_mfr / std::max(MFR_EPS, baseline_residual);

    if (mfr.residual_ratio > MFR_MAX_RESIDUAL_RATIO) {
        mfr.miss_override_reason = "MISS_MFR_NoOverride_ResidualTooHigh";
        return mfr;
    }

    // Radius stability: compare against board center (0,0) reference
    // For misses there's no baseline radius, so we check against board bounds
    double max_radius_shift = MFR_MAX_RADIUS_SHIFT_FRAC * 1.0;  // board_radius normalized = 1.0
    if (radius_mfr > 1.0 + max_radius_shift) {
        mfr.miss_override_reason = "MISS_MFR_NoOverride_RadialShift";
        return mfr;
    }

    // ---------------------------------------------------------------
    // STEP 6: Accept Override
    // ---------------------------------------------------------------
    mfr.miss_override_applied = true;
    mfr.miss_override_reason = "MISS_MFR_Override_StrongCams";
    mfr.final_segment = tri_override->segment;
    mfr.final_multiplier = tri_override->multiplier;
    mfr.final_score = tri_override->score;
    mfr.override_result = *tri_override;
    mfr.override_result->method = "MISS_MFR_Override_StrongCams";

    return mfr;
}

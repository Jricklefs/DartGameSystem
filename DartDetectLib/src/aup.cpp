/**
 * aup.cpp - Phase 24: Angular Uncertainty Propagation
 *
 * Post-processing step that may change the wedge (segment) selection
 * when theta_final is near a wedge boundary. Uses per-camera angular
 * spread to estimate uncertainty, then computes Gaussian probability
 * mass over candidate wedges.
 *
 * Does NOT modify radius, ring, multiplier, or any calibration.
 */
#include "dart_detect_internal.h"
#include <cmath>
#include <algorithm>
#include <vector>
#include <string>

// ============================================================================
// Feature flags (default OFF)
// ============================================================================
static bool g_use_aup = false;
static bool g_aup_enable_boundary_prob = true;
static bool g_aup_only_near_boundary = true;
static bool g_aup_require_camera_evidence = true;
static bool g_aup_fallback_if_unstable = true;

int set_aup_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseAUP") { g_use_aup = (value != 0); return 0; }
    if (s == "AUP_EnableBoundaryProbSelection") { g_aup_enable_boundary_prob = (value != 0); return 0; }
    if (s == "AUP_OnlyWhenNearBoundary") { g_aup_only_near_boundary = (value != 0); return 0; }
    if (s == "AUP_RequireCameraEvidence") { g_aup_require_camera_evidence = (value != 0); return 0; }
    if (s == "AUP_FallbackToBaselineIfUnstable") { g_aup_fallback_if_unstable = (value != 0); return 0; }
    return -1;
}

bool aup_is_enabled() { return g_use_aup; }

// ============================================================================
// Parameters
// ============================================================================
static constexpr double AUP_EPS = 1e-6;
static constexpr double NEAR_BOUNDARY_DEG = 2.0;
static constexpr double MIN_SIGMA_DEG = 0.6;
static constexpr double MAX_SIGMA_DEG = 6.0;
static constexpr double GAUSS_CLAMP_SIGMA_DEG = 0.5;
static constexpr int    MIN_EFFECTIVE_CAMERAS = 2;
static constexpr double MAX_CAMERA_THETA_SPREAD_DEG = 10.0;
static constexpr double PROB_MARGIN_RATIO = 1.10;
static constexpr int    MAX_ALLOWED_WEDGE_STEP = 1;

// ============================================================================
// Helpers
// ============================================================================

static double norm360(double a) {
    a = std::fmod(a, 360.0);
    return a < 0.0 ? a + 360.0 : a;
}

static double circ_diff_deg(double a, double b) {
    double d = std::fmod(a - b + 540.0, 360.0) - 180.0;
    return d;
}

static double gauss_cdf(double x, double mu, double sigma) {
    return 0.5 * (1.0 + std::erf((x - mu) / (sigma * std::sqrt(2.0))));
}

// Probability mass of Normal(mu, sigma) over wedge interval [lo, hi]
// Uses unwrapping relative to mu for circular handling
static double gauss_interval_prob(double mu, double sigma, double lo, double hi) {
    // Unwrap lo and hi relative to mu
    double d_lo = circ_diff_deg(lo, mu);
    double d_hi = circ_diff_deg(hi, mu);
    // Ensure d_lo < d_hi
    if (d_hi <= d_lo) d_hi += 18.0; // wedge is 18 degrees
    if (d_hi - d_lo > 180.0) return 0.0;
    double p_hi = gauss_cdf(mu + d_hi, mu, sigma);
    double p_lo = gauss_cdf(mu + d_lo, mu, sigma);
    return std::max(0.0, p_hi - p_lo);
}

// ============================================================================
// Wedge geometry helpers
// ============================================================================

static int wedge_index_from_angle(double angle_deg) {
    double adj = std::fmod(angle_deg - 90.0 + 9.0 + 360.0, 360.0);
    return ((int)(adj / 18.0)) % 20;
}

// Lower boundary angle of wedge in board-space degrees
static double wedge_lower_boundary(int wedge_idx) {
    // adjusted_angle = angle_deg - 81; wedge_idx = floor(adjusted/18)
    // So adjusted_lo = wedge_idx * 18, angle_deg = adjusted + 81
    double adj_lo = wedge_idx * 18.0;
    return norm360(adj_lo + 81.0);
}

static double wedge_upper_boundary(int wedge_idx) {
    double adj_hi = (wedge_idx + 1) * 18.0;
    return norm360(adj_hi + 81.0);
}

// ============================================================================
// Main AUP function
// ============================================================================

AupResult run_aup(
    double theta_final_deg,
    int wedge_primary_idx,
    const std::vector<double>& per_camera_theta_deg)
{
    AupResult r;
    r.theta_final = theta_final_deg;
    r.wedge_primary = wedge_primary_idx;
    r.wedge_final = wedge_primary_idx;
    r.aup_applied = false;

    if (!g_use_aup) {
        r.method = "AUP_Disabled";
        return r;
    }

    // STEP 1: Boundary proximity
    double adj = std::fmod(theta_final_deg - 90.0 + 9.0 + 360.0, 360.0);
    double frac = std::fmod(adj, 18.0);
    double boundary_dist = std::min(frac, 18.0 - frac);
    r.boundary_distance_deg = boundary_dist;

    if (g_aup_only_near_boundary && boundary_dist > NEAR_BOUNDARY_DEG) {
        r.method = "AUP_Skip_NotNearBoundary";
        return r;
    }

    // STEP 2: Estimate angular uncertainty
    int n_cams = (int)per_camera_theta_deg.size();
    if (n_cams < MIN_EFFECTIVE_CAMERAS) {
        r.method = "AUP_Skip_InsufficientCams";
        return r;
    }

    // Circular mean
    double sum_sin = 0, sum_cos = 0;
    for (double t : per_camera_theta_deg) {
        double rad = t * CV_PI / 180.0;
        sum_sin += std::sin(rad);
        sum_cos += std::cos(rad);
    }
    double theta_mean_rad = std::atan2(sum_sin / n_cams, sum_cos / n_cams);
    double theta_mean_deg = theta_mean_rad * 180.0 / CV_PI;
    if (theta_mean_deg < 0) theta_mean_deg += 360.0;

    // Camera theta spread
    double max_spread = 0;
    for (int i = 0; i < n_cams; i++) {
        for (int j = i + 1; j < n_cams; j++) {
            double d = std::abs(circ_diff_deg(per_camera_theta_deg[i], per_camera_theta_deg[j]));
            max_spread = std::max(max_spread, d);
        }
    }
    r.theta_spread_deg = max_spread;

    if (g_aup_require_camera_evidence && max_spread > MAX_CAMERA_THETA_SPREAD_DEG) {
        r.method = "AUP_Skip_CamDisagreement";
        return r;
    }

    // Circular std dev
    double sum_sq = 0;
    for (double t : per_camera_theta_deg) {
        double d = circ_diff_deg(t, theta_mean_deg);
        sum_sq += d * d;
    }
    double sigma_deg = std::sqrt(sum_sq / n_cams);
    sigma_deg = std::max(GAUSS_CLAMP_SIGMA_DEG, std::min(MAX_SIGMA_DEG, sigma_deg));
    r.sigma_theta_deg = sigma_deg;

    if (sigma_deg < MIN_SIGMA_DEG) {
        r.method = "AUP_Skip_SigmaTooLow";
        return r;
    }

    // STEP 4: Safety fallback
    if (g_aup_fallback_if_unstable && sigma_deg > MAX_SIGMA_DEG) {
        r.method = "AUP_Fallback_SigmaTooHigh";
        return r;
    }

    // STEP 3: Probability mass for candidate wedges
    if (!g_aup_enable_boundary_prob) {
        r.method = "AUP_Skip_ProbDisabled";
        return r;
    }

    int left_idx = (wedge_primary_idx - 1 + 20) % 20;
    int right_idx = (wedge_primary_idx + 1) % 20;

    auto compute_prob = [&](int widx) -> double {
        double lo = wedge_lower_boundary(widx);
        double hi = wedge_upper_boundary(widx);
        return gauss_interval_prob(theta_final_deg, sigma_deg, lo, hi);
    };

    double p_primary = compute_prob(wedge_primary_idx);
    double p_left = compute_prob(left_idx);
    double p_right = compute_prob(right_idx);

    r.P_primary = p_primary;
    r.P_left = p_left;
    r.P_right = p_right;

    // Find best
    int best_idx = wedge_primary_idx;
    double best_prob = p_primary;
    if (p_left > best_prob) { best_prob = p_left; best_idx = left_idx; }
    if (p_right > best_prob) { best_prob = p_right; best_idx = right_idx; }

    r.prob_ratio = best_prob / std::max(AUP_EPS, p_primary);

    if (best_idx != wedge_primary_idx && r.prob_ratio >= PROB_MARGIN_RATIO) {
        r.wedge_final = best_idx;
        r.aup_applied = true;
        r.method = "AUP_ProbabilisticWedgeSelect";
    } else {
        r.wedge_final = wedge_primary_idx;
        r.method = "AUP_KeepPrimary";
    }

    return r;
}

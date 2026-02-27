/**
 * triangulation.cpp - Line intersection triangulation + TPS homography
 * 
 * Ported from Python: routes.py triangulate_with_line_intersection(),
 * build_perspective_transform(), warp_point()
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <map>
#include <cmath>
#include <set>
#include <opencv2/calib3d.hpp>
// === FEATURE FLAG: Dart Centerline V2 ===
static bool g_use_robust_triangulation = true;

// === FEATURE FLAGS: Triangulation Confidence (7-Phase Upgrade) ===
static bool g_use_perpendicular_residual_gating = true;  // Phase 1
static bool g_use_barrel_signal_gate = true;              // Phase 3
static bool g_use_board_radius_gate = true;               // Board radius miss override

static constexpr double R_SOFT = 1.015;
static constexpr double R_HARD = 1.030;

static bool g_use_wire_boundary_voting = true;
static constexpr double WIRE_EPS_DEG = 0.50;
static constexpr double WIRE_HARD_EPS_DEG = 0.25;

// === FEATURE FLAGS: Phase 10 BCWT ===
static bool g_use_bcwt = false;
static bool g_bcwt_allow_soft_include = true;
static double g_bcwt_min_weight = 0.15;
static double g_bcwt_max_weight_cap = 1.0;

// === FEATURE FLAGS: Phase 10B BCWT Radial Stability Clamp ===
static bool g_use_bcwt_radial_clamp = false;
static int g_radial_clamp_mode = 0;  // 0=fallback_to_bestpair, 1=hybrid
static bool g_radial_clamp_only_near_rings = true;
static bool g_radial_clamp_respect_miss = true;
static double RADIAL_DELTA_THRESHOLD = 0.030;
static double NEAR_RING_EPS = 0.020;

// === FEATURE FLAGS: Phase 11C BCWT Circular Angular Fusion (v2) ===
static bool g_use_caf = false;
static bool g_caf_only_near_wedge_boundaries = true;
static bool g_caf_require_camera_agreement = true;
static bool g_caf_use_bestpair_as_prior = true;
static bool g_caf_fallback_bestpair_on_disagreement = true;
static bool g_caf_require_residual_non_regression = true;
static int g_caf_min_effective_camera_count = 2;
static double g_caf_max_camera_theta_spread_deg = 6.0;
static double g_caf_prior_weight = 0.35;
static double g_caf_max_fused_theta_delta_vs_bcwt_deg = 8.0;
static double g_caf_min_residual_improvement_ratio = 0.90;
static double g_caf_tangential_eps = 0.002;
static constexpr double CAF_EPS = 1e-6;
// Relaxed residual gating params
static double g_caf_residual_allow_soft_worsen = 1.05;
static bool g_caf_soft_worsen_only_if_adjacent_wedge = true;
static bool g_caf_soft_worsen_only_if_near_boundary = true;
static bool g_caf_soft_worsen_require_support = true;

// Ring boundary radii in normalized space (radius / 170.0)
static const double RING_RADII[] = {
    6.35 / 170.0,   // bullseye outer
    16.0 / 170.0,   // bull outer
    99.0 / 170.0,   // triple inner
    107.0 / 170.0,  // triple outer
    162.0 / 170.0,  // double inner
    170.0 / 170.0,  // double outer (board edge)
};
static const int NUM_RING_RADII = 6;

static bool near_any_ring(double r) {
    for (int i = 0; i < NUM_RING_RADII; ++i) {
        if (std::abs(r - RING_RADII[i]) <= NEAR_RING_EPS) return true;
    }
    return false;
}

// Phase 10: Flag setter for triangulation flags (called from skeleton.cpp)
int set_triangulation_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseBarrelConfidenceWeightedTriangulation") { g_use_bcwt = (value != 0); return 0; }
    if (s == "BCWT_AllowSoftIncludeWeakCam") { g_bcwt_allow_soft_include = (value != 0); return 0; }
    if (s == "BCWT_MinWeightToInclude") { g_bcwt_min_weight = value / 100.0; return 0; }  // pass as int percent
    if (s == "BCWT_MaxWeightCap") { g_bcwt_max_weight_cap = value / 100.0; return 0; }
    if (s == "UseBCWTRadialStabilityClamp") { g_use_bcwt_radial_clamp = (value != 0); return 0; }
    if (s == "RadialClamp_Mode") { g_radial_clamp_mode = value; return 0; }
    if (s == "RadialClamp_OnlyNearRings") { g_radial_clamp_only_near_rings = (value != 0); return 0; }
    if (s == "RadialClamp_RespectMissOverride") { g_radial_clamp_respect_miss = (value != 0); return 0; }
    if (s == "RadialClamp_DeltaThreshold") { RADIAL_DELTA_THRESHOLD = value / 1000.0; return 0; }
    if (s == "RadialClamp_NearRingEps") { NEAR_RING_EPS = value / 1000.0; return 0; }
    // Phase 11C CAF flags
    if (s == "UseBCWTCircularAngularFusion") { g_use_caf = (value != 0); return 0; }
    if (s == "CAF_OnlyNearWedgeBoundaries") { g_caf_only_near_wedge_boundaries = (value != 0); return 0; }
    if (s == "CAF_RequireCameraAgreement") { g_caf_require_camera_agreement = (value != 0); return 0; }
    if (s == "CAF_UseBestPairAsPrior") { g_caf_use_bestpair_as_prior = (value != 0); return 0; }
    if (s == "CAF_FallbackToBestPairOnDisagreement") { g_caf_fallback_bestpair_on_disagreement = (value != 0); return 0; }
    if (s == "CAF_RequireResidualNonRegression") { g_caf_require_residual_non_regression = (value != 0); return 0; }
    if (s == "CAF_MinEffectiveCameraCount") { g_caf_min_effective_camera_count = value; return 0; }
    if (s == "CAF_MaxCameraThetaSpreadDeg") { g_caf_max_camera_theta_spread_deg = value / 10.0; return 0; }
    if (s == "CAF_PriorWeight") { g_caf_prior_weight = value / 100.0; return 0; }
    if (s == "CAF_MaxFusedThetaDeltaDeg") { g_caf_max_fused_theta_delta_vs_bcwt_deg = value / 10.0; return 0; }
    if (s == "CAF_MinResidualImprovementRatio") { g_caf_min_residual_improvement_ratio = value / 100.0; return 0; }
    if (s == "CAF_TangentialEps") { g_caf_tangential_eps = value / 10000.0; return 0; }
    return -1;  // not our flag
}




// ============================================================================
// TPS (Thin-Plate Spline) Transform
// ============================================================================

// TPS radial basis function: r-¦ * log(r)
static double tps_basis(double r)
{
    if (r < 1e-10) return 0.0;
    return r * r * std::log(r);
}

static double tps_basis_dist(double x1, double y1, double x2, double y2)
{
    double r = std::sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    return tps_basis(r);
}

// Sample a point on an ellipse by casting a ray from board center at given angle
static std::optional<Point2f> sample_ellipse_at_angle(
    const EllipseData& ell, double angle_rad, double bcx, double bcy)
{
    double a = ell.width / 2.0;
    double b = ell.height / 2.0;
    double rot = ell.rotation_deg * CV_PI / 180.0;
    double cos_r = std::cos(rot), sin_r = std::sin(rot);
    
    double dx = std::cos(angle_rad);
    double dy = std::sin(angle_rad);
    
    // Transform ray to ellipse-local coords
    double ox = bcx - ell.cx, oy = bcy - ell.cy;
    double u0 = ox * cos_r + oy * sin_r;
    double du = dx * cos_r + dy * sin_r;
    double v0 = -ox * sin_r + oy * cos_r;
    double dv = -dx * sin_r + dy * cos_r;
    
    // Quadratic for ray-ellipse intersection
    double A = du * du / (a * a) + dv * dv / (b * b);
    double B = 2.0 * (u0 * du / (a * a) + v0 * dv / (b * b));
    double C = u0 * u0 / (a * a) + v0 * v0 / (b * b) - 1.0;
    double disc = B * B - 4 * A * C;
    if (disc < 0) return std::nullopt;
    
    double sqrt_disc = std::sqrt(disc);
    double t1 = (-B + sqrt_disc) / (2 * A);
    double t2 = (-B - sqrt_disc) / (2 * A);
    double t = (std::min(t1, t2) < 0) ? std::max(t1, t2) : std::min(t1, t2);
    if (t <= 0) t = std::max(t1, t2);
    if (t <= 0) return std::nullopt;
    
    return Point2f(bcx + t * dx, bcy + t * dy);
}

TpsTransform build_tps_transform(const CameraCalibration& cal)
{
    TpsTransform tps;
    tps.valid = false;
    
    if (cal.segment_angles.size() < 20) return tps;
    
    double bcx = cal.center.x, bcy = cal.center.y;
    int seg20_idx = cal.segment_20_index;
    
    // Ring configs: (ellipse_data, normalized_radius)
    struct RingConfig {
        const std::optional<EllipseData>* ellipse;
        double norm_radius;
    };
    
    std::vector<RingConfig> rings = {
        {&cal.outer_double_ellipse,  170.0 / 170.0},
        {&cal.inner_double_ellipse,  162.0 / 170.0},
        {&cal.outer_triple_ellipse,  107.0 / 170.0},
        {&cal.inner_triple_ellipse,   99.0 / 170.0},
        {&cal.bull_ellipse,           16.0 / 170.0},
        {&cal.bullseye_ellipse,       6.35 / 170.0},
    };
    
    std::vector<double> src_x, src_y, dst_x, dst_y;
    
    for (const auto& ring : rings) {
        if (!ring.ellipse->has_value()) continue;
        const auto& ell = ring.ellipse->value();
        
        for (int idx = 0; idx < 20; ++idx) {
            auto px_pt = sample_ellipse_at_angle(ell, cal.segment_angles[idx], bcx, bcy);
            if (!px_pt) continue;
            
            src_x.push_back(px_pt->x);
            src_y.push_back(px_pt->y);
            
            int board_idx = ((idx - seg20_idx) % 20 + 20) % 20;
            double angle_cw_deg = board_idx * 18.0 - 9.0;
            double angle_cw_rad = angle_cw_deg * CV_PI / 180.0;
            dst_x.push_back(ring.norm_radius * std::sin(angle_cw_rad));
            dst_y.push_back(ring.norm_radius * std::cos(angle_cw_rad));
        }
    }
    // === MID-RING TPS CONTROL POINTS (Feb 19, 2026) ===
    // The original TPS used only the 6 standard dartboard rings as control points
    // (121 total: 6 rings x 20 angles + center). This left large normalized-radius
    // gaps with zero constraints: 0.488 between bull and triple-inner, and 0.324
    // between triple-outer and double-inner. The TPS warp was inaccurate in these
    // gap regions, causing darts landing in the single-bed zones to score incorrectly.
    // Adding interpolated rings at the midpoints (bull+triple)/2 and (triple+double)/2
    // gives ~40 more control points (161 total), significantly improving warp accuracy.
    // Benchmark: +1 dart accuracy across test games.

    
        // Add mid-ring interpolated control points for smoother TPS in gap regions
    // Mid bull-to-triple_inner and mid triple_outer-to-double_inner
    struct MidRingConfig {
        const std::optional<EllipseData>* inner_ell;
        const std::optional<EllipseData>* outer_ell;
        double norm_radius;
    };
    std::vector<MidRingConfig> mid_rings = {
        {&cal.bull_ellipse, &cal.inner_triple_ellipse, (16.0 + 99.0) / 2.0 / 170.0},
        {&cal.outer_triple_ellipse, &cal.inner_double_ellipse, (107.0 + 162.0) / 2.0 / 170.0},
    };
    for (const auto& mr : mid_rings) {
        if (!mr.inner_ell->has_value() || !mr.outer_ell->has_value()) continue;
        const auto& ell_in = mr.inner_ell->value();
        const auto& ell_out = mr.outer_ell->value();
        for (int idx = 0; idx < 20; ++idx) {
            auto pt_in = sample_ellipse_at_angle(ell_in, cal.segment_angles[idx], bcx, bcy);
            auto pt_out = sample_ellipse_at_angle(ell_out, cal.segment_angles[idx], bcx, bcy);
            if (!pt_in || !pt_out) continue;
            src_x.push_back((pt_in->x + pt_out->x) / 2.0);
            src_y.push_back((pt_in->y + pt_out->y) / 2.0);
            int board_idx = ((idx - seg20_idx) % 20 + 20) % 20;
            double angle_cw_deg = board_idx * 18.0 - 9.0;
            double angle_cw_rad = angle_cw_deg * CV_PI / 180.0;
            dst_x.push_back(mr.norm_radius * std::sin(angle_cw_rad));
            dst_y.push_back(mr.norm_radius * std::cos(angle_cw_rad));
        }
    }

        // Add center anchor
    src_x.push_back(bcx); src_y.push_back(bcy);
    dst_x.push_back(0.0); dst_y.push_back(0.0);
    
    int N = (int)src_x.size();
    if (N < 4) return tps;
    
    // Build TPS system: solve for weights
    // TPS: f(x) = a0 + a1*x + a2*y + sum(w_i * phi(|x - x_i|))
    // where phi(r) = r-¦ * log(r)
    //
    // System matrix (N+3) x (N+3):
    // [K  P] [w]   [v]
    // [P' 0] [a] = [0]
    //
    // K_ij = phi(|src_i - src_j|), P = [1, x, y]
    
    cv::Mat K(N, N, CV_64F);
    for (int i = 0; i < N; ++i)
        for (int j = 0; j < N; ++j)
            K.at<double>(i, j) = tps_basis_dist(src_x[i], src_y[i], src_x[j], src_y[j]);
    
    cv::Mat P(N, 3, CV_64F);
    for (int i = 0; i < N; ++i) {
        P.at<double>(i, 0) = 1.0;
        P.at<double>(i, 1) = src_x[i];
        P.at<double>(i, 2) = src_y[i];
    }
    
    // Build full system matrix L = [K P; P' 0]
    int M = N + 3;
    cv::Mat L = cv::Mat::zeros(M, M, CV_64F);
    K.copyTo(L(cv::Range(0, N), cv::Range(0, N)));
    P.copyTo(L(cv::Range(0, N), cv::Range(N, M)));
    cv::Mat(P.t()).copyTo(L(cv::Range(N, M), cv::Range(0, N)));
    
    // RHS: [dst_x; 0 0 0] and [dst_y; 0 0 0]
    cv::Mat rhs_x = cv::Mat::zeros(M, 1, CV_64F);
    cv::Mat rhs_y = cv::Mat::zeros(M, 1, CV_64F);
    for (int i = 0; i < N; ++i) {
        rhs_x.at<double>(i) = dst_x[i];
        rhs_y.at<double>(i) = dst_y[i];
    }
    
    // Solve
    cv::Mat sol_x, sol_y;
    bool ok1 = cv::solve(L, rhs_x, sol_x, cv::DECOMP_SVD);
    bool ok2 = cv::solve(L, rhs_y, sol_y, cv::DECOMP_SVD);
    
    if (!ok1 || !ok2) return tps;
    
    // Store results
    tps.src_points = cv::Mat(N, 2, CV_64F);
    tps.dst_points = cv::Mat(N, 2, CV_64F);
    for (int i = 0; i < N; ++i) {
        tps.src_points.at<double>(i, 0) = src_x[i];
        tps.src_points.at<double>(i, 1) = src_y[i];
        tps.dst_points.at<double>(i, 0) = dst_x[i];
        tps.dst_points.at<double>(i, 1) = dst_y[i];
    }
    
    // weights: Nx2 (w) + 3x2 (a0, a1, a2)
    tps.weights = cv::Mat(M, 2, CV_64F);
    for (int i = 0; i < M; ++i) {
        tps.weights.at<double>(i, 0) = sol_x.at<double>(i);
        tps.weights.at<double>(i, 1) = sol_y.at<double>(i);
    }
    
    tps.valid = true;
    return tps;
}

Point2f TpsTransform::transform(double px, double py) const
{
    if (!valid) return Point2f(0, 0);
    
    int N = src_points.rows;
    
    // f(x) = a0 + a1*px + a2*py + sum(w_i * phi(|p - src_i|))
    double result_x = 0, result_y = 0;
    
    // TPS kernel contributions
    for (int i = 0; i < N; ++i) {
        double sx = src_points.at<double>(i, 0);
        double sy = src_points.at<double>(i, 1);
        double phi = tps_basis_dist(px, py, sx, sy);
        result_x += weights.at<double>(i, 0) * phi;
        result_y += weights.at<double>(i, 1) * phi;
    }
    
    // Affine part: a0 + a1*x + a2*y
    result_x += weights.at<double>(N, 0) + weights.at<double>(N + 1, 0) * px + weights.at<double>(N + 2, 0) * py;
    result_y += weights.at<double>(N, 1) + weights.at<double>(N + 1, 1) * px + weights.at<double>(N + 2, 1) * py;
    
    return Point2f(result_x, result_y);
}

Point2f warp_point(const TpsTransform& tps, double px, double py)
{
    return tps.transform(px, py);
}

// ============================================================================
// 2D Line Intersection
// ============================================================================

std::optional<Point2f> intersect_lines_2d(
    double x1, double y1, double x2, double y2,
    double x3, double y3, double x4, double y4)
{
    double denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
    double len1 = std::hypot(x2 - x1, y2 - y1);
    double len2 = std::hypot(x4 - x3, y4 - y3);
    if (len1 < 1e-12 || len2 < 1e-12) return std::nullopt;
    
    // Reject near-parallel lines: sin(crossing angle) < 0.26 (~15 degrees)
    double sin_angle = std::abs(denom) / (len1 * len2);
    if (sin_angle < 0.26) return std::nullopt;
    
    double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
    double ix = x1 + t * (x2 - x1);
    double iy = y1 + t * (y2 - y1);
    
    // Reject intersections far off the board (>1.5 board radii)
    if (std::hypot(ix, iy) > 1.5) return std::nullopt;
    
    return Point2f(ix, iy);
}

// ============================================================================
// Line Intersection Triangulation (Autodarts-style)
// ============================================================================



// === Phase 10: BCWT Per-Camera Confidence Weight ===
static double clamp01(double x) { return std::max(0.0, std::min(1.0, x)); }

struct BcwtCamWeight {
    double w_final = 0.0;
    double pix_score = 0.0;
    double asp_score = 0.0;
    double inl_score = 0.0;
    double ang_pca_score = 0.0;
    double ang_ft_score = 0.0;
    double tip_score = 0.0;
    double mask_score_val = 0.0;
    bool cam_invalid = false;
    bool dropped_by_legacy = false;
    bool included_by_bcwt = false;
};

static BcwtCamWeight bcwt_compute_weight(const DetectionResult& det, double mask_quality) {
    BcwtCamWeight bw;
    
    if (det.barrel_pixel_count == 0 || det.barrel_aspect_ratio == 0.0) {
        bw.cam_invalid = true;
        bw.w_final = 0.0;
        return bw;
    }
    
    bw.pix_score = clamp01(det.barrel_pixel_count / 200.0);
    bw.asp_score = clamp01((det.barrel_aspect_ratio - 2.0) / 4.0);
    
    // ransac inlier ratio - if not RANSAC, default 0.5
    double inl = det.ransac_inlier_ratio;
    if (inl <= 0.0) inl = 0.5;
    bw.inl_score = clamp01((inl - 0.35) / 0.45);
    
    // Phase 9 fields not yet on Dev4.0 � use defaults per spec
    bw.ang_pca_score = 0.7;
    bw.ang_ft_score = 0.7;
    bw.tip_score = 0.8;
    
    // mask_quality
    bw.mask_score_val = (mask_quality > 0.0) ? clamp01(mask_quality) : 0.7;
    
    double w_raw = 0.20 * bw.pix_score
                 + 0.15 * bw.asp_score
                 + 0.15 * bw.inl_score
                 + 0.15 * bw.ang_pca_score
                 + 0.10 * bw.ang_ft_score
                 + 0.10 * bw.tip_score
                 + 0.15 * bw.mask_score_val;
    
    bw.w_final = clamp01(w_raw) * g_bcwt_max_weight_cap;
    return bw;
}

// === Phase V2-5: Robust Least-Squares Point from Lines ===
// Given N lines in 2D (each defined by point + direction), find the point
// minimizing sum of squared distances to all lines, with Huber loss for outlier suppression.
static std::optional<Point2f> robust_least_squares_point(
    const std::vector<std::pair<Point2f, Point2f>>& lines_start_end,
    const std::vector<double>& weights,
    int max_iter = 5, double huber_k = 0.1)
{
    int N = (int)lines_start_end.size();
    if (N < 2) return std::nullopt;
    
    // Each line: point p_i, direction d_i -> normal n_i = (-d_iy, d_ix)
    // Distance from point x to line i: n_i . (x - p_i)
    // Minimize sum w_i * rho(n_i . (x - p_i))
    
    // First pass: standard weighted least squares
    // n_i . x = n_i . p_i  =>  A x = b where A[i] = n_i, b[i] = n_i . p_i
    // Normal equations: (A^T W A) x = A^T W b
    
    // Build A and b
    std::vector<double> nx(N), ny(N), rhs(N), w(N);
    for (int i = 0; i < N; ++i) {
        double dx = lines_start_end[i].second.x - lines_start_end[i].first.x;
        double dy = lines_start_end[i].second.y - lines_start_end[i].first.y;
        double len = std::sqrt(dx * dx + dy * dy);
        if (len < 1e-12) { nx[i] = 0; ny[i] = 0; rhs[i] = 0; w[i] = 0; continue; }
        nx[i] = -dy / len;
        ny[i] = dx / len;
        rhs[i] = nx[i] * lines_start_end[i].first.x + ny[i] * lines_start_end[i].first.y;
        w[i] = (i < (int)weights.size()) ? weights[i] : 1.0;
    }
    
    double sol_x = 0, sol_y = 0;
    
    for (int iter = 0; iter < max_iter; ++iter) {
        // Weighted normal equations: (A^T W A) x = A^T W b
        double ata00 = 0, ata01 = 0, ata11 = 0;
        double atb0 = 0, atb1 = 0;
        
        for (int i = 0; i < N; ++i) {
            double wi = w[i];
            if (wi < 1e-12) continue;
            
            // Huber reweighting (after first iteration)
            if (iter > 0) {
                double res = nx[i] * sol_x + ny[i] * sol_y - rhs[i];
                double abs_res = std::abs(res);
                if (abs_res > huber_k) {
                    wi *= huber_k / abs_res;  // Huber downweight
                }
            }
            
            ata00 += wi * nx[i] * nx[i];
            ata01 += wi * nx[i] * ny[i];
            ata11 += wi * ny[i] * ny[i];
            atb0 += wi * nx[i] * rhs[i];
            atb1 += wi * ny[i] * rhs[i];
        }
        
        double det = ata00 * ata11 - ata01 * ata01;
        if (std::abs(det) < 1e-12) return std::nullopt;
        
        sol_x = (ata11 * atb0 - ata01 * atb1) / det;
        sol_y = (ata00 * atb1 - ata01 * atb0) / det;
    }
    
    return Point2f(sol_x, sol_y);
}

std::optional<IntersectionResult> triangulate_with_line_intersection(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations)
{
    // Step 1: Build TPS transforms and warp lines per camera
    struct CamLine {
        Point2f line_start, line_end;
        Point2f tip_normalized;
        Point2f tip_pixel;
        ScoreResult vote;
        bool tip_reliable;
        double tip_dist;
        double mask_quality;
        double detection_quality;  // Phase 4A: combined quality weight
        // Phase 1/2/7: warped board-space line direction
        double warped_dir_x = 0.0;
        double warped_dir_y = 0.0;
        // Phase 3/7: barrel signal metrics
        int barrel_pixel_count = 0;
        double barrel_aspect_ratio = 0.0;
        bool weak_barrel_signal = false;
    };
    
    std::map<std::string, CamLine> cam_lines;
    
    for (const auto& [cam_id, det] : camera_results) {
        if (!det.pca_line || !det.tip) continue;
        
        auto cal_it = calibrations.find(cam_id);
        if (cal_it == calibrations.end()) continue;
        
        // TPS is precomputed at init time (see dd_init in dart_detect.cpp),
        // not per-detection. This avoids the O(n^3) TPS solve on every dart.
const TpsTransform& tps = cal_it->second.tps_cache;
        if (!tps.valid) continue;
        
        double vx = det.pca_line->vx, vy = det.pca_line->vy;
        double x0 = det.pca_line->x0, y0 = det.pca_line->y0;
        
        // === EXPERIMENT: Homography warp instead of TPS multi-point ===
        cv::Mat H_mat;
        {
            std::vector<cv::Point2f> sv, dv;
            for (int hi = 0; hi < tps.src_points.rows; ++hi) {
                sv.push_back(cv::Point2f((float)tps.src_points.at<double>(hi, 0),
                                          (float)tps.src_points.at<double>(hi, 1)));
                dv.push_back(cv::Point2f((float)tps.dst_points.at<double>(hi, 0),
                                          (float)tps.dst_points.at<double>(hi, 1)));
            }
            H_mat = cv::findHomography(sv, dv, cv::RANSAC, 5.0);
        }

        double extent = 200.0;
        std::vector<cv::Point2f> warped_sample_pts;
        const int N_SAMPLES = 21;
        
        if (!H_mat.empty()) {
            std::vector<cv::Point2f> src_pts_h;
            for (int t = 0; t < N_SAMPLES; ++t) {
                double frac = (double)t / (N_SAMPLES - 1);
                double dist_back = extent * (1.0 - frac);
                src_pts_h.push_back(cv::Point2f(
                    (float)(det.tip->x - vx * dist_back),
                    (float)(det.tip->y - vy * dist_back)));
            }
            cv::perspectiveTransform(src_pts_h, warped_sample_pts, H_mat);
        } else {
            for (int t = 0; t < N_SAMPLES; ++t) {
                double frac = (double)t / (N_SAMPLES - 1);
                double dist_back = extent * (1.0 - frac);
                double px = det.tip->x - vx * dist_back;
                double py = det.tip->y - vy * dist_back;
                Point2f wp = warp_point(tps, px, py);
                warped_sample_pts.push_back(cv::Point2f(wp.x, wp.y));
            }
        }

        Point2f tip_n = warp_point(tps, det.tip->x, det.tip->y);
        cv::Vec4f warped_line_fit;
        cv::fitLine(warped_sample_pts, warped_line_fit, cv::DIST_HUBER, 0, 0.01, 0.01);
        double wvx = warped_line_fit[0], wvy = warped_line_fit[1];
        
        Point2f p2_n = tip_n;
        Point2f p1_n(tip_n.x - wvx * 2.0, tip_n.y - wvy * 2.0);
        
        // Per-camera vote using ellipse scoring
        ScoreResult vote = score_from_ellipse_calibration(
            det.tip->x, det.tip->y, cal_it->second);
        
        double tip_dist = std::sqrt(tip_n.x * tip_n.x + tip_n.y * tip_n.y);
        bool tip_reliable = tip_dist <= 1.2;
        
        // Phase 4A: Compute detection quality weight
        double dq_inlier = std::max(0.3, std::min(1.0, det.ransac_inlier_ratio));
        double dq_pixels = std::min(1.0, det.barrel_pixel_count / 200.0);
        double dq_aspect = std::min(1.0, det.barrel_aspect_ratio / 8.0);
        double detection_quality = 0.5 * dq_inlier + 0.3 * dq_pixels + 0.2 * dq_aspect;
        detection_quality = std::max(0.1, detection_quality);

        // Phase 3: Barrel metric consistency fix
        bool weak_barrel_signal = false;
        if (det.barrel_pixel_count == 0) {
            detection_quality *= 0.5;
            weak_barrel_signal = true;
        }

        // Store warped direction for Phase 1/2
        double wdir_len = std::sqrt(wvx * wvx + wvy * wvy);
        double norm_wvx = (wdir_len > 1e-12) ? wvx / wdir_len : 0.0;
        double norm_wvy = (wdir_len > 1e-12) ? wvy / wdir_len : 0.0;

        CamLine cl;
        cl.line_start = p1_n;
        cl.line_end = p2_n;
        cl.tip_normalized = tip_n;
        cl.tip_pixel = Point2f(det.tip->x, det.tip->y);
        // Note: tps is a const ref to cached transform, copy not needed for CamLine lifetime
        cl.vote = vote;
        cl.tip_reliable = tip_reliable;
        cl.tip_dist = tip_dist;
        cl.mask_quality = det.mask_quality;
        cl.detection_quality = detection_quality;
        cl.warped_dir_x = norm_wvx;
        cl.warped_dir_y = norm_wvy;
        cl.barrel_pixel_count = det.barrel_pixel_count;
        cl.barrel_aspect_ratio = det.barrel_aspect_ratio;
        cl.weak_barrel_signal = weak_barrel_signal;
        cam_lines[cam_id] = std::move(cl);
    }
    
    if (cam_lines.size() < 2) return std::nullopt;

    // === Phase 3: Barrel Signal Gate ===
    if (g_use_barrel_signal_gate && cam_lines.size() >= 2) {
        bool all_weak = true;
        for (const auto& [cid, cl] : cam_lines) {
            if (cl.barrel_pixel_count >= 40 || cl.barrel_aspect_ratio >= 2.2) {
                all_weak = false;
                break;
            }
        }
        if (all_weak) {
            IntersectionResult result;
            result.segment = 0; result.multiplier = 0; result.score = 0;
            result.method = "MissOverride_BarrelSignal";
            result.confidence = 0.8;
            result.coords = Point2f(0, 0);
            result.total_error = 0;
            for (const auto& [cam_id, data] : cam_lines)
                result.per_camera[cam_id] = data.vote;
            return result;
        }
    }
    
    // Step 2: Pairwise intersections
    std::vector<std::string> cam_ids;
    for (const auto& [id, _] : cam_lines) cam_ids.push_back(id);
    std::sort(cam_ids.begin(), cam_ids.end());
    
    struct Intersection {
        std::string cam1, cam2;
        Point2f coords;
        double err1, err2, total_error;
        ScoreResult score;
        bool both_reliable, ix_on_board;
        double ix_dist;
    };
    
    std::vector<Intersection> intersections;
    
    for (size_t i = 0; i < cam_ids.size(); ++i) {
        for (size_t j = i + 1; j < cam_ids.size(); ++j) {
            const auto& l1 = cam_lines[cam_ids[i]];
            const auto& l2 = cam_lines[cam_ids[j]];
            
            auto ix = intersect_lines_2d(
                l1.line_start.x, l1.line_start.y, l1.line_end.x, l1.line_end.y,
                l2.line_start.x, l2.line_start.y, l2.line_end.x, l2.line_end.y);
            
            if (!ix) continue;
            
            double e1_raw = std::sqrt(
                (ix->x - l1.tip_normalized.x) * (ix->x - l1.tip_normalized.x) +
                (ix->y - l1.tip_normalized.y) * (ix->y - l1.tip_normalized.y));
            double e2_raw = std::sqrt(
                (ix->x - l2.tip_normalized.x) * (ix->x - l2.tip_normalized.x) +
                (ix->y - l2.tip_normalized.y) * (ix->y - l2.tip_normalized.y));
            // Phase 4A: Scale errors inversely by detection quality
            double e1 = e1_raw / std::max(0.1, l1.detection_quality);
            double e2 = e2_raw / std::max(0.1, l2.detection_quality);
            
            // Score in normalized board space
            double ix_dist = std::sqrt(ix->x * ix->x + ix->y * ix->y);
            double ix_angle_rad = std::atan2(ix->y, -ix->x);  // negate x per Python
            double ix_angle_deg = ix_angle_rad * 180.0 / CV_PI;
            if (ix_angle_deg < 0) ix_angle_deg += 360.0;
            ix_angle_deg = std::fmod(ix_angle_deg, 360.0);
            
            ScoreResult score = score_from_polar(ix_angle_deg, ix_dist);
            
            intersections.push_back(Intersection{
                cam_ids[i], cam_ids[j], *ix,
                e1, e2, e1 + e2,
                score,
                l1.tip_reliable && l2.tip_reliable,
                ix_dist <= 1.3,
                ix_dist
            });
        }
    }
    
    if (intersections.empty()) return std::nullopt;

    // === Phase V2-5: Robust Least-Squares Integration ===
    // === Phase 10: BCWT weights ===
    std::map<std::string, BcwtCamWeight> bcwt_weights;
    std::optional<Point2f> robust_point;
    std::optional<Point2f> bcwt_point;
    std::string bcwt_method_used = "legacy";
    
    if (g_use_bcwt) {
        // Compute BCWT weights for all cameras
        for (const auto& cid : cam_ids) {
            const auto& cl = cam_lines[cid];
            // Need DetectionResult for this cam
            auto det_it = camera_results.find(cid);
            if (det_it == camera_results.end()) continue;
            BcwtCamWeight bw = bcwt_compute_weight(det_it->second, cl.mask_quality);
            bcwt_weights[cid] = bw;
        }
        
        // Determine included cameras
        std::vector<std::pair<Point2f, Point2f>> bcwt_lines;
        std::vector<double> bcwt_w;
        std::vector<std::string> bcwt_included_ids;
        
        for (const auto& cid : cam_ids) {
            auto& bw = bcwt_weights[cid];
            if (bw.cam_invalid) continue;
            
            bool legacy_would_drop = cam_lines[cid].weak_barrel_signal;
            bw.dropped_by_legacy = legacy_would_drop;
            
            if (g_bcwt_allow_soft_include) {
                // Include if weight >= min threshold, even if legacy would drop
                if (bw.w_final >= g_bcwt_min_weight) {
                    bw.included_by_bcwt = true;
                    const auto& cl = cam_lines[cid];
                    bcwt_lines.push_back({cl.line_start, cl.line_end});
                    bcwt_w.push_back(bw.w_final);
                    bcwt_included_ids.push_back(cid);
                }
            } else {
                // Use existing drop logic but replace binary with weight
                if (!legacy_would_drop && bw.w_final >= g_bcwt_min_weight) {
                    bw.included_by_bcwt = true;
                    const auto& cl = cam_lines[cid];
                    bcwt_lines.push_back({cl.line_start, cl.line_end});
                    bcwt_w.push_back(bw.w_final);
                    bcwt_included_ids.push_back(cid);
                }
            }
        }
        
        // Check angle spread among included lines
        double bcwt_angle_spread = 0.0;
        if (bcwt_included_ids.size() >= 2) {
            std::vector<double> bcwt_angles;
            for (const auto& cid : bcwt_included_ids) {
                const auto& cl = cam_lines[cid];
                double a = std::atan2(cl.warped_dir_y, cl.warped_dir_x) * 180.0 / CV_PI;
                bcwt_angles.push_back(a);
            }
            std::sort(bcwt_angles.begin(), bcwt_angles.end());
            bcwt_angle_spread = bcwt_angles.back() - bcwt_angles.front();
            if (bcwt_angle_spread > 180.0) bcwt_angle_spread = 360.0 - bcwt_angle_spread;
        }
        
        // Solve BCWT if >= 2 cameras and sufficient angle spread
        if (bcwt_lines.size() >= 2 && bcwt_angle_spread >= 15.0) {
            bcwt_point = robust_least_squares_point(bcwt_lines, bcwt_w, 5, 0.01);
            if (bcwt_point) {
                double rp_dist = std::sqrt(bcwt_point->x * bcwt_point->x + bcwt_point->y * bcwt_point->y);
                if (rp_dist <= 1.3) {
                    bcwt_method_used = "BCWT";
                } else {
                    bcwt_point.reset();  // off board, fall back
                }
            }
        }
        
        // Also compute legacy robust point as fallback
        if (cam_lines.size() >= 2) {
            std::vector<std::pair<Point2f, Point2f>> all_lines;
            std::vector<double> all_weights;
            for (const auto& cid : cam_ids) {
                const auto& cl = cam_lines[cid];
                all_lines.push_back({cl.line_start, cl.line_end});
                all_weights.push_back(cl.detection_quality * cl.mask_quality);
            }
            robust_point = robust_least_squares_point(all_lines, all_weights);
        }
    } else if (g_use_robust_triangulation && cam_lines.size() >= 2) {
        std::vector<std::pair<Point2f, Point2f>> all_lines;
        std::vector<double> all_weights;
        for (const auto& cid : cam_ids) {
            const auto& cl = cam_lines[cid];
            all_lines.push_back({cl.line_start, cl.line_end});
            all_weights.push_back(cl.detection_quality * cl.mask_quality);
        }
        robust_point = robust_least_squares_point(all_lines, all_weights);
    }


    
    // Step 3: Voting hierarchy
    // Per-camera segment votes
    std::map<std::string, int> cam_votes;
    for (const auto& [cam_id, data] : cam_lines) {
        cam_votes[cam_id] = data.vote.segment;
    }
    
    // Phase 4A: Count votes weighted by detection quality
    std::map<int, double> vote_weights;
    std::map<int, int> vote_counts;
    for (const auto& [cam_id, seg] : cam_votes) {
        vote_counts[seg]++;
        vote_weights[seg] += cam_lines[cam_id].detection_quality;
    }
    
    int most_common_seg = 0, most_common_count = 0;
    double most_common_weight = 0.0;
    for (const auto& [seg, cnt] : vote_counts) {
        if (cnt > most_common_count || (cnt == most_common_count && vote_weights[seg] > most_common_weight)) {
            most_common_seg = seg;
            most_common_count = cnt;
            most_common_weight = vote_weights[seg];
        }
    }
    
    // Select best intersection based on voting hierarchy
    const Intersection* best = nullptr;
    std::string method;
    double confidence;
    
    if (most_common_count == (int)cam_votes.size() && (int)cam_votes.size() >= 3) {
        // UnanimousCam
        best = &*std::min_element(intersections.begin(), intersections.end(),
            [](const auto& a, const auto& b) { return a.total_error < b.total_error; });
        method = "UnanimousCam";
        confidence = 0.95;
    } else if (most_common_count >= 2) {
        // Cam+1: prefer intersections involving agreeing cameras
        std::set<std::string> agreeing;
        for (const auto& [cid, seg] : cam_votes) {
            if (seg == most_common_seg) agreeing.insert(cid);
        }
        
        std::vector<const Intersection*> agreeing_ix;
        for (const auto& ix : intersections) {
            if (agreeing.count(ix.cam1) || agreeing.count(ix.cam2))
                agreeing_ix.push_back(&ix);
        }
        
        if (!agreeing_ix.empty()) {
            best = *std::min_element(agreeing_ix.begin(), agreeing_ix.end(),
                [](const auto* a, const auto* b) { return a->total_error < b->total_error; });
        } else {
            best = &*std::min_element(intersections.begin(), intersections.end(),
                [](const auto& a, const auto& b) { return a.total_error < b.total_error; });
        }
        method = "Cam+1";
        confidence = 0.8;
    } else {
        // BestError
        best = &*std::min_element(intersections.begin(), intersections.end(),
            [](const auto& a, const auto& b) { return a.total_error < b.total_error; });
        method = "BestError";
        confidence = 0.5;
    }
    
    // Miss detection: two independent checks
    // Check 1: If selected intersection is far off-board (>1.3 norm dist), force miss
    if (best->ix_dist > 1.3) {
        IntersectionResult result;
        result.segment = 0; result.multiplier = 0; result.score = 0;
        result.method = "MissOverride_IxDist";
        result.confidence = 0.7;
        result.coords = best->coords;
        result.total_error = best->total_error;
        for (const auto& [cam_id, data] : cam_lines)
            result.per_camera[cam_id] = data.vote;
        return result;
    }
    // Check 2: For doubles, if ALL cameras see tip off-board, force miss
    // (doubles at board edge are most prone to surround dart false positives)
    if (best->score.multiplier == 2) {
        int off_board_count = 0;
        int total_cams = 0;
        for (const auto& [_, data] : cam_lines) {
            ++total_cams;
            if (data.tip_dist > 1.05) ++off_board_count;
        }
        if (off_board_count == total_cams && total_cams >= 2) {
            IntersectionResult result;
            result.segment = 0; result.multiplier = 0; result.score = 0;
            result.method = "MissOverride_AllCams";
            result.confidence = 0.7;
            result.coords = best->coords;
            result.total_error = best->total_error;
            for (const auto& [cam_id, data] : cam_lines)
                result.per_camera[cam_id] = data.vote;
            return result;
        }
    }
    

    // Phase V2-5 / Phase 10: Use BCWT or robust point if available
    Point2f final_coords = best->coords;
    if (g_use_bcwt && bcwt_point) {
        // BCWT is primary when enabled and valid
        final_coords = *bcwt_point;
    } else if (g_use_robust_triangulation && robust_point) {
        double rp_dist = std::sqrt(robust_point->x * robust_point->x + robust_point->y * robust_point->y);
        if (rp_dist <= 1.3) {
            // Use robust point, but only if it's close to the pairwise result
            double dx = robust_point->x - best->coords.x;
            double dy = robust_point->y - best->coords.y;
            double disagreement = std::sqrt(dx * dx + dy * dy);
            if (disagreement < 0.15) {
                final_coords = *robust_point;
            }
        }
    }

    // === Phase 10B: BCWT Radial Stability Clamp ===
    bool radial_clamp_applied = false;
    std::string radial_clamp_reason;
    std::string radial_clamp_method;
    double r_bcwt_10b = 0, r_bestpair_10b = 0, radial_delta_10b = 0;
    bool near_ring_bcwt = false, near_ring_best = false, near_ring_any_10b = false;
    Point2f x_bestpair_10b(0,0);
    Point2f x_preclamp = final_coords;
    
    if (g_use_bcwt && g_use_bcwt_radial_clamp && bcwt_point) {
        // x_bestpair = legacy robust point or best pairwise intersection
        if (robust_point) {
            double rp_d = std::sqrt(robust_point->x * robust_point->x + robust_point->y * robust_point->y);
            if (rp_d <= 1.3) x_bestpair_10b = *robust_point;
            else x_bestpair_10b = best->coords;
        } else {
            x_bestpair_10b = best->coords;
        }
        
        r_bcwt_10b = std::sqrt(bcwt_point->x * bcwt_point->x + bcwt_point->y * bcwt_point->y);
        r_bestpair_10b = std::sqrt(x_bestpair_10b.x * x_bestpair_10b.x + x_bestpair_10b.y * x_bestpair_10b.y);
        radial_delta_10b = std::abs(r_bcwt_10b - r_bestpair_10b);
        
        near_ring_bcwt = near_any_ring(r_bcwt_10b);
        near_ring_best = near_any_ring(r_bestpair_10b);
        near_ring_any_10b = near_ring_bcwt || near_ring_best;
        
        if (!g_radial_clamp_only_near_rings) near_ring_any_10b = true;
        
        if (near_ring_any_10b && radial_delta_10b > RADIAL_DELTA_THRESHOLD) {
            radial_clamp_applied = true;
            if (g_radial_clamp_mode == 0) {
                // Mode A: fallback to bestpair
                final_coords = x_bestpair_10b;
                radial_clamp_reason = "radial_delta";
                radial_clamp_method = "BestPair_Fallback_RadialClamp";
            } else {
                // Mode B: hybrid - BCWT angle, bestpair radius
                double theta_bcwt = std::atan2(bcwt_point->y, bcwt_point->x);
                final_coords = Point2f(std::cos(theta_bcwt) * r_bestpair_10b,
                                       std::sin(theta_bcwt) * r_bestpair_10b);
                radial_clamp_reason = "radial_delta_hybrid";
                radial_clamp_method = "BCWT_HybridAngle_RadiusBestPair";
            }
        }
    }

    // === Phase 11C v2: Circular Angular Fusion (CAF) with Relaxed Residual Gating ===
    bool caf_applied = false;
    std::string caf_method;
    double theta_bcwt_deg_caf = 0, theta_best_deg_caf = 0, theta_fused_deg_caf = 0;
    double theta_spread_deg_caf = 0, delta_fused_vs_bcwt_caf = 0;
    int wedge_bcwt_caf = 0, wedge_best_caf = 0, wedge_caf_val = 0;
    double residual_bcwt_caf = 0, residual_caf_val = 0, improvement_ratio_caf = 0;
    Point2f x_caf_pt(0,0);
    int caf_effective_cam_count = 0;
    bool caf_near_boundary = false;
    int caf_wedge_distance = 0;
    bool caf_soft_accepted = false;

    // Helper: get wedge segment from a normalized-space point
    auto wedge_from_point = [](const Point2f& p) -> int {
        double d = std::sqrt(p.x * p.x + p.y * p.y);
        double a_rad = std::atan2(p.y, -p.x);
        double a_deg = a_rad * 180.0 / CV_PI;
        if (a_deg < 0) a_deg += 360.0;
        a_deg = std::fmod(a_deg, 360.0);
        ScoreResult sr = score_from_polar(a_deg, d);
        return sr.segment;
    };

    // Helper: get wedge INDEX (0-19) from a normalized-space point
    auto wedge_index_from_point = [](const Point2f& p) -> int {
        double a_rad = std::atan2(p.y, -p.x);
        double a_deg = a_rad * 180.0 / CV_PI;
        if (a_deg < 0) a_deg += 360.0;
        a_deg = std::fmod(a_deg, 360.0);
        double adj = std::fmod(a_deg - 90.0 + 9.0 + 360.0, 360.0);
        return ((int)(adj / 18.0)) % 20;
    };

    // Helper: minimal circular wedge distance (on 20-segment ring)
    auto wedge_dist = [](int w1, int w2) -> int {
        int d = std::abs(w1 - w2);
        return std::min(d, 20 - d);
    };

    // Helper: compute perpendicular residual at a given point
    auto compute_residual_at = [&](const Point2f& pt) -> double {
        std::vector<double> resids;
        for (const auto& cid : cam_ids) {
            const auto& cl = cam_lines[cid];
            double nx = -cl.warped_dir_y;
            double ny = cl.warped_dir_x;
            double dx = pt.x - cl.line_end.x;
            double dy = pt.y - cl.line_end.y;
            double r = std::abs(nx * dx + ny * dy);
            resids.push_back(r);
        }
        std::sort(resids.begin(), resids.end());
        return resids[resids.size() / 2];  // median
    };

    if (g_use_bcwt && g_use_caf && bcwt_point) {
        Point2f x_bcwt_caf = final_coords;
        Point2f x_bp_caf = x_bestpair_10b;

        double theta_bcwt_rad = std::atan2(x_bcwt_caf.y, x_bcwt_caf.x);
        double theta_best_rad = std::atan2(x_bp_caf.y, x_bp_caf.x);
        theta_bcwt_deg_caf = theta_bcwt_rad * 180.0 / CV_PI;
        theta_best_deg_caf = theta_best_rad * 180.0 / CV_PI;

        wedge_bcwt_caf = wedge_from_point(x_bcwt_caf);
        wedge_best_caf = wedge_from_point(x_bp_caf);
        int widx_bcwt = wedge_index_from_point(x_bcwt_caf);
        int widx_best = wedge_index_from_point(x_bp_caf);

        for (const auto& [cid, bw] : bcwt_weights) {
            if (bw.included_by_bcwt) caf_effective_cam_count++;
        }

        bool caf_skip = false;

        // Step 1: Segmentation-based near-boundary detection
        if (!caf_skip && g_caf_only_near_wedge_boundaries) {
            int w0 = wedge_from_point(x_bcwt_caf);
            double r_bcwt = std::sqrt(x_bcwt_caf.x * x_bcwt_caf.x + x_bcwt_caf.y * x_bcwt_caf.y);
            if (r_bcwt > CAF_EPS) {
                double tx = -x_bcwt_caf.y / r_bcwt;
                double ty = x_bcwt_caf.x / r_bcwt;
                double eps = g_caf_tangential_eps;
                Point2f x_plus(x_bcwt_caf.x + eps * tx, x_bcwt_caf.y + eps * ty);
                Point2f x_minus(x_bcwt_caf.x - eps * tx, x_bcwt_caf.y - eps * ty);
                int w_plus = wedge_from_point(x_plus);
                int w_minus = wedge_from_point(x_minus);
                caf_near_boundary = (w_plus != w0) || (w_minus != w0);
                if (!caf_near_boundary) {
                    caf_method = "BCWT_NoCAF_NotNearBoundary";
                    caf_skip = true;
                }
            }
        } else {
            caf_near_boundary = true;  // if filter off, treat as near boundary
        }

        // Step 2: Require min camera count
        if (!caf_skip && caf_effective_cam_count < g_caf_min_effective_camera_count) {
            caf_method = "BCWT_NoCAF_InsufficientCameras";
            caf_skip = true;
        }

        // Step 3: Camera agreement check
        if (!caf_skip && g_caf_require_camera_agreement) {
            std::vector<double> cam_thetas;
            for (const auto& [cid, bw] : bcwt_weights) {
                if (!bw.included_by_bcwt) continue;
                const auto& cl = cam_lines[cid];
                double th = std::atan2(cl.line_end.y, cl.line_end.x) * 180.0 / CV_PI;
                cam_thetas.push_back(th);
            }
            if (cam_thetas.size() >= 2) {
                std::vector<double> sorted_th = cam_thetas;
                for (auto& th : sorted_th) th = std::fmod(th + 360.0, 360.0);
                std::sort(sorted_th.begin(), sorted_th.end());
                double max_gap = 0;
                for (size_t i = 1; i < sorted_th.size(); ++i)
                    max_gap = std::max(max_gap, sorted_th[i] - sorted_th[i-1]);
                max_gap = std::max(max_gap, 360.0 - sorted_th.back() + sorted_th.front());
                theta_spread_deg_caf = 360.0 - max_gap;
                if (theta_spread_deg_caf > g_caf_max_camera_theta_spread_deg) {
                    if (g_caf_fallback_bestpair_on_disagreement) {
                        final_coords = x_bp_caf;
                        caf_method = "BestPair_Fallback_CAF_Disagreement";
                    } else {
                        caf_method = "BCWT_NoCAF_Disagreement";
                    }
                    caf_skip = true;
                }
            }
        }

        // Step 4: Circular Angular Fusion
        if (!caf_skip) {
            double vx_sum = 0, vy_sum = 0;
            for (const auto& [cid, bw] : bcwt_weights) {
                if (!bw.included_by_bcwt) continue;
                const auto& cl = cam_lines[cid];
                double theta_i = std::atan2(cl.line_end.y, cl.line_end.x);
                vx_sum += bw.w_final * std::cos(theta_i);
                vy_sum += bw.w_final * std::sin(theta_i);
            }
            if (g_caf_use_bestpair_as_prior) {
                vx_sum += g_caf_prior_weight * std::cos(theta_best_rad);
                vy_sum += g_caf_prior_weight * std::sin(theta_best_rad);
            }
            double theta_fused_rad = std::atan2(vy_sum, vx_sum);
            theta_fused_deg_caf = theta_fused_rad * 180.0 / CV_PI;
            double d = std::abs(theta_fused_deg_caf - theta_bcwt_deg_caf);
            if (d > 180.0) d = 360.0 - d;
            delta_fused_vs_bcwt_caf = d;
            if (delta_fused_vs_bcwt_caf > g_caf_max_fused_theta_delta_vs_bcwt_deg) {
                caf_method = "BCWT_NoCAF_DeltaTooLarge";
                caf_skip = true;
            }
        }

        // Step 5: Angle-only correction (preserve Phase 10B radius)
        if (!caf_skip) {
            double r_final = std::sqrt(final_coords.x * final_coords.x + final_coords.y * final_coords.y);
            double theta_fused_rad = theta_fused_deg_caf * CV_PI / 180.0;
            x_caf_pt = Point2f(r_final * std::cos(theta_fused_rad), r_final * std::sin(theta_fused_rad));
            wedge_caf_val = wedge_from_point(x_caf_pt);
            int widx_caf = wedge_index_from_point(x_caf_pt);
            caf_wedge_distance = wedge_dist(widx_caf, widx_bcwt);

            // Step 6: RELAXED Residual Gating (v2 multi-tier)
            if (g_caf_require_residual_non_regression) {
                residual_bcwt_caf = compute_residual_at(final_coords);
                residual_caf_val = compute_residual_at(x_caf_pt);
                improvement_ratio_caf = residual_caf_val / std::max(CAF_EPS, residual_bcwt_caf);

                bool accept = false;

                // Tier 1: STRICT ACCEPT (residual improved or equal)
                if (improvement_ratio_caf <= 1.0) {
                    accept = true;
                }

                // Tier 2: SOFT ACCEPT (up to 5% residual worsening, with conditions)
                if (!accept && improvement_ratio_caf <= g_caf_residual_allow_soft_worsen) {
                    bool soft_ok = true;
                    if (g_caf_soft_worsen_only_if_near_boundary && !caf_near_boundary)
                        soft_ok = false;
                    if (soft_ok && g_caf_soft_worsen_only_if_adjacent_wedge && caf_wedge_distance != 1)
                        soft_ok = false;
                    if (soft_ok && g_caf_soft_worsen_require_support) {
                        // Support: CAF wedge matches best-pair OR camera wedge majority
                        std::map<int, int> cam_wedge_counts;
                        for (const auto& [cid, bw] : bcwt_weights) {
                            if (!bw.included_by_bcwt) continue;
                            int cw = wedge_from_point(cam_lines[cid].line_end);
                            cam_wedge_counts[cw]++;
                        }
                        int majority_wedge = 0, majority_count = 0;
                        for (const auto& [w, c] : cam_wedge_counts) {
                            if (c > majority_count) { majority_count = c; majority_wedge = w; }
                        }
                        bool support_ok = (wedge_caf_val == wedge_best_caf) || (wedge_caf_val == majority_wedge);
                        if (!support_ok) soft_ok = false;
                    }
                    if (soft_ok) {
                        accept = true;
                        caf_soft_accepted = true;
                    }
                }

                // Tier 3: WEDGE-CROSS (stronger condition when crossing wedges)
                if (!accept && wedge_caf_val != wedge_bcwt_caf) {
                    if (improvement_ratio_caf <= g_caf_min_residual_improvement_ratio) {
                        accept = true;
                    }
                    // Also accept if CAF agrees with best-pair wedge
                    if (!accept && wedge_caf_val == wedge_best_caf) {
                        accept = true;
                    }
                }

                if (!accept) {
                    if (g_caf_fallback_bestpair_on_disagreement && wedge_bcwt_caf != wedge_best_caf) {
                        final_coords = x_bp_caf;
                        caf_method = "BestPair_Fallback_CAF_Rejected";
                    } else {
                        caf_method = "BCWT_NoCAF_Rejected";
                    }
                    caf_skip = true;
                }
            }
        }

        // Step 7: Accept CAF
        if (!caf_skip) {
            final_coords = x_caf_pt;
            caf_method = "BCWT_CAF_AngleFusion";
            caf_applied = true;
        }
    }

    // Score the final coordinates
    double final_dist = std::sqrt(final_coords.x * final_coords.x + final_coords.y * final_coords.y);
    double final_angle_rad = std::atan2(final_coords.y, -final_coords.x);
    double final_angle_deg = final_angle_rad * 180.0 / CV_PI;
    if (final_angle_deg < 0) final_angle_deg += 360.0;
    final_angle_deg = std::fmod(final_angle_deg, 360.0);
    ScoreResult final_score = score_from_polar(final_angle_deg, final_dist);

    // === Phase 1: Perpendicular Residual Metric ===
    std::vector<double> perp_residuals;
    std::map<std::string, double> per_cam_residual;
    for (const auto& cid : cam_ids) {
        const auto& cl = cam_lines[cid];
        // normal = (-dir_y, dir_x)
        double nx = -cl.warped_dir_y;
        double ny = cl.warped_dir_x;
        // point on line = line_end (which is tip_normalized)
        double dx = final_coords.x - cl.line_end.x;
        double dy = final_coords.y - cl.line_end.y;
        double residual = std::abs(nx * dx + ny * dy);
        perp_residuals.push_back(residual);
        per_cam_residual[cid] = residual;
    }
    std::sort(perp_residuals.begin(), perp_residuals.end());
    double median_residual = perp_residuals[perp_residuals.size() / 2];
    double max_residual = perp_residuals.back();
    double residual_spread = max_residual - perp_residuals.front();

    // === Phase 2: Board-Space Angular Spread ===
    std::vector<double> angles;
    for (const auto& cid : cam_ids) {
        const auto& cl = cam_lines[cid];
        double angle_deg = std::atan2(cl.warped_dir_y, cl.warped_dir_x) * 180.0 / CV_PI;
        angles.push_back(angle_deg);
    }
    std::sort(angles.begin(), angles.end());
    double angle_spread = angles.back() - angles.front();
    // Handle wraparound: if spread > 180, use complementary
    if (angle_spread > 180.0) angle_spread = 360.0 - angle_spread;

    // === Phase 4: Camera Outlier Rejection ===
    bool camera_dropped = false;
    std::string dropped_cam_id;
    if (cam_ids.size() >= 3 && max_residual > 2.0 * median_residual) {
        // Find the camera with max residual
        std::string worst_cam;
        double worst_res = 0;
        for (const auto& [cid, res] : per_cam_residual) {
            if (res > worst_res) { worst_res = res; worst_cam = cid; }
        }
        // Recompute robust least-squares without that camera
        std::vector<std::pair<Point2f, Point2f>> reduced_lines;
        std::vector<double> reduced_weights;
        for (const auto& cid : cam_ids) {
            if (cid == worst_cam) continue;
            const auto& cl = cam_lines[cid];
            reduced_lines.push_back({cl.line_start, cl.line_end});
            reduced_weights.push_back(cl.detection_quality * cl.mask_quality);
        }
        auto recomputed = robust_least_squares_point(reduced_lines, reduced_weights);
        if (recomputed) {
            double rp_dist = std::sqrt(recomputed->x * recomputed->x + recomputed->y * recomputed->y);
            if (rp_dist <= 1.3) {
                final_coords = *recomputed;
                camera_dropped = true;
                dropped_cam_id = worst_cam;
                // Rescore
                final_dist = rp_dist;
                final_angle_rad = std::atan2(final_coords.y, -final_coords.x);
                final_angle_deg = final_angle_rad * 180.0 / CV_PI;
                if (final_angle_deg < 0) final_angle_deg += 360.0;
                final_angle_deg = std::fmod(final_angle_deg, 360.0);
                final_score = score_from_polar(final_angle_deg, final_dist);
            }
        }
    }


    // === Wire Boundary Voting ===
    double adjusted_for_wire = std::fmod(final_angle_deg - 90.0 + 9.0 + 360.0, 360.0);
    double frac_wire = std::fmod(adjusted_for_wire, 18.0);
    double boundary_distance_deg = std::min(frac_wire, 18.0 - frac_wire);
    int base_wedge_idx = ((int)(adjusted_for_wire / 18.0)) % 20;
    int neighbor_wedge_idx;
    if (frac_wire < 9.0)
        neighbor_wedge_idx = (base_wedge_idx - 1 + 20) % 20;
    else
        neighbor_wedge_idx = (base_wedge_idx + 1) % 20;

    bool is_wire_ambiguous = (boundary_distance_deg < WIRE_EPS_DEG);
    std::string wedge_chosen_by = "direct";
    std::map<int, int> wedge_votes_map;
    double winner_pct = 1.0;
    double vote_margin = 1.0;
    std::string wire_low_conf;

    if (is_wire_ambiguous && g_use_wire_boundary_voting) {
        double sigma = std::clamp(2.0 * median_residual, 0.001, 0.010);
        static const double offsets[][2] = {
            {+1,0},{-1,0},{0,+1},{0,-1},
            {+1,+1},{+1,-1},{-1,+1},{-1,-1},
            {+2,0},{-2,0},{0,+2},{0,-2},
            {+2,+1},{+2,-1},{-2,+1},{-2,-1}
        };
        wedge_votes_map[base_wedge_idx] = 1;
        for (int k = 0; k < 16; ++k) {
            double px = final_coords.x + offsets[k][0] * sigma;
            double py = final_coords.y + offsets[k][1] * sigma;
            double a_rad = std::atan2(py, -px);
            double a_deg = a_rad * 180.0 / CV_PI;
            if (a_deg < 0) a_deg += 360.0;
            a_deg = std::fmod(a_deg, 360.0);
            double adj = std::fmod(a_deg - 90.0 + 9.0 + 360.0, 360.0);
            int w = ((int)(adj / 18.0)) % 20;
            if (w == base_wedge_idx || w == neighbor_wedge_idx) {
                wedge_votes_map[w]++;
            } else {
                wedge_votes_map[base_wedge_idx]++;
            }
        }
        int total_votes = 0;
        int winner_wedge = base_wedge_idx;
        int winner_count = 0;
        for (auto& [w, c] : wedge_votes_map) {
            total_votes += c;
            if (c > winner_count) { winner_count = c; winner_wedge = w; }
        }
        int runner_up_count = total_votes - winner_count;
        winner_pct = (total_votes > 0) ? (double)winner_count / total_votes : 1.0;
        vote_margin = (total_votes > 0) ? (double)(winner_count - runner_up_count) / total_votes : 1.0;
        if (winner_pct >= 0.65) {
            if (winner_wedge != base_wedge_idx) {
                final_score.segment = SEGMENT_ORDER[winner_wedge];
                final_score.score = final_score.segment * final_score.multiplier;
                wedge_chosen_by = "wire_vote";
            } else {
                wedge_chosen_by = "wire_vote";
            }
        } else if (boundary_distance_deg < WIRE_HARD_EPS_DEG) {
            if (winner_wedge != base_wedge_idx) {
                final_score.segment = SEGMENT_ORDER[winner_wedge];
                final_score.score = final_score.segment * final_score.multiplier;
                wedge_chosen_by = "wire_vote";
            }
            wire_low_conf = "WireBoundaryAmbiguity";
        } else {
            wedge_chosen_by = "direct";
            wire_low_conf = "WireBoundaryAmbiguity";
        }
    }

    // === Phase 5: Confidence Score ===
    double avg_dq = 0;
    for (const auto& cid : cam_ids) avg_dq += cam_lines[cid].detection_quality;
    avg_dq /= cam_ids.size();
    double computed_confidence = std::exp(-5.0 * median_residual)
        * std::min(1.0, std::max(0.0, angle_spread / 60.0))
        * avg_dq;

    // === Phase 1 Gating + Phase 2 Gating + Phase 5 Gating ===
    bool force_miss = false;
    if (g_use_perpendicular_residual_gating) {
        if (max_residual > 0.18) {
            force_miss = true;
        } else if (max_residual > 0.12) {
            confidence = std::min(confidence, 0.3);  // LOW_CONFIDENCE
        }
    }
    // Phase 2 gating
    if (angle_spread < 20.0 && median_residual > 0.10) {
        force_miss = true;
    } else if (angle_spread < 25.0 && median_residual > 0.06) {
        confidence = std::min(confidence, 0.3);
    }
    // Phase 5 gating
    if (computed_confidence < 0.35) {
        confidence = std::min(confidence, 0.3);
    }

    if (force_miss) {
        IntersectionResult result;
        result.segment = 0; result.multiplier = 0; result.score = 0;
        result.method = "MissOverride_Residual";
        result.confidence = computed_confidence;
        result.coords = final_coords;
        result.total_error = best->total_error;
        for (const auto& [cam_id, data] : cam_lines)
            result.per_camera[cam_id] = data.vote;
        return result;
    }

    // Phase 6: Use computed_confidence as primary (tip-based error kept for diagnostics only)
    confidence = std::min(confidence, std::max(0.1, computed_confidence));

    // === Board Radius Miss Override ===
    double board_radius = std::sqrt(final_coords.x*final_coords.x + final_coords.y*final_coords.y);
    std::string radius_gate_reason;
    if (g_use_board_radius_gate) {
        if (board_radius > R_HARD) {
            IntersectionResult result;
            result.segment = 0; result.multiplier = 0; result.score = 0;
            result.method = "MissOverride_RadiusHard";
            result.confidence = 0;
            result.coords = final_coords;
            result.total_error = best->total_error;
            for (const auto& [cam_id, data] : cam_lines)
                result.per_camera[cam_id] = data.vote;
            // Add tri_debug to early return
            {
                IntersectionResult::TriangulationDebug td;
                td.board_radius = board_radius;
                td.radius_gate_reason = "RadiusHard";
                td.median_residual = median_residual;
                td.max_residual = max_residual;
                td.residual_spread = residual_spread;
                td.angle_spread_deg = angle_spread;
                td.final_confidence = computed_confidence;
                td.camera_dropped = camera_dropped;
                td.dropped_cam_id = dropped_cam_id;
                for (const auto& [cid2, cl2] : cam_lines) {
                    IntersectionResult::TriangulationDebug::CamDebug cd2;
                    cd2.warped_dir_x = cl2.warped_dir_x; cd2.warped_dir_y = cl2.warped_dir_y;
                    cd2.perp_residual = per_cam_residual.count(cid2) ? per_cam_residual.at(cid2) : 0;
                    cd2.barrel_pixel_count = cl2.barrel_pixel_count;
                    cd2.barrel_aspect_ratio = cl2.barrel_aspect_ratio;
                    cd2.detection_quality = cl2.detection_quality;
                    cd2.weak_barrel_signal = cl2.weak_barrel_signal;
                    cd2.warped_point_x = cl2.line_end.x; cd2.warped_point_y = cl2.line_end.y;
                    td.cam_debug[cid2] = cd2;
                }
                result.tri_debug = td;
            }
            return result;
        } else if (board_radius > R_SOFT && confidence < 0.55) {
            IntersectionResult result;
            result.segment = 0; result.multiplier = 0; result.score = 0;
            result.method = "MissOverride_RadiusSoftLowConf";
            result.confidence = 0;
            result.coords = final_coords;
            result.total_error = best->total_error;
            for (const auto& [cam_id, data] : cam_lines)
                result.per_camera[cam_id] = data.vote;
            // Add tri_debug to early return
            {
                IntersectionResult::TriangulationDebug td;
                td.board_radius = board_radius;
                td.radius_gate_reason = "RadiusSoftLowConf";
                td.median_residual = median_residual;
                td.max_residual = max_residual;
                td.residual_spread = residual_spread;
                td.angle_spread_deg = angle_spread;
                td.final_confidence = computed_confidence;
                td.camera_dropped = camera_dropped;
                td.dropped_cam_id = dropped_cam_id;
                for (const auto& [cid2, cl2] : cam_lines) {
                    IntersectionResult::TriangulationDebug::CamDebug cd2;
                    cd2.warped_dir_x = cl2.warped_dir_x; cd2.warped_dir_y = cl2.warped_dir_y;
                    cd2.perp_residual = per_cam_residual.count(cid2) ? per_cam_residual.at(cid2) : 0;
                    cd2.barrel_pixel_count = cl2.barrel_pixel_count;
                    cd2.barrel_aspect_ratio = cl2.barrel_aspect_ratio;
                    cd2.detection_quality = cl2.detection_quality;
                    cd2.weak_barrel_signal = cl2.weak_barrel_signal;
                    cd2.warped_point_x = cl2.line_end.x; cd2.warped_point_y = cl2.line_end.y;
                    td.cam_debug[cid2] = cd2;
                }
                result.tri_debug = td;
            }
            return result;
        } else if (board_radius > R_SOFT) {
            confidence = std::min(confidence, 0.3);
            radius_gate_reason = "RadiusSoft";
        }
    }

    // === Phase 7: Build debug struct ===
    IntersectionResult::TriangulationDebug tri_dbg;
    tri_dbg.angle_spread_deg = angle_spread;
    tri_dbg.median_residual = median_residual;
    tri_dbg.max_residual = max_residual;
    tri_dbg.residual_spread = residual_spread;
    tri_dbg.final_confidence = computed_confidence;
    tri_dbg.camera_dropped = camera_dropped;
    tri_dbg.dropped_cam_id = dropped_cam_id;
    tri_dbg.board_radius = board_radius;
    tri_dbg.radius_gate_reason = radius_gate_reason;
    for (const auto& cid : cam_ids) {
        const auto& cl = cam_lines[cid];
        IntersectionResult::TriangulationDebug::CamDebug cd;
        cd.warped_dir_x = cl.warped_dir_x;
        cd.warped_dir_y = cl.warped_dir_y;
        cd.perp_residual = per_cam_residual[cid];
        cd.barrel_pixel_count = cl.barrel_pixel_count;
        cd.barrel_aspect_ratio = cl.barrel_aspect_ratio;
        cd.detection_quality = cl.detection_quality;
        cd.weak_barrel_signal = cl.weak_barrel_signal;
        cd.warped_point_x = cl.line_end.x;
        cd.warped_point_y = cl.line_end.y;
        tri_dbg.cam_debug[cid] = cd;
    }

    IntersectionResult result;
    result.segment = final_score.segment;
    result.multiplier = final_score.multiplier;
    result.score = final_score.score;
    // Phase 2: Segment label / multiplier consistency
    bool segment_label_corrected = false;
    if (final_score.zone == "double" && result.multiplier != 2) {
        result.multiplier = 2; result.score = result.segment * 2;
        segment_label_corrected = true;
    } else if (final_score.zone == "triple" && result.multiplier != 3) {
        result.multiplier = 3; result.score = result.segment * 3;
        segment_label_corrected = true;
    } else if (final_score.zone == "single" && result.multiplier != 1) {
        result.multiplier = 1; result.score = result.segment * 1;
        segment_label_corrected = true;
    }

    tri_dbg.segment_label_corrected = segment_label_corrected;
    tri_dbg.boundary_distance_deg = boundary_distance_deg;
    tri_dbg.is_wire_ambiguous = is_wire_ambiguous;
    tri_dbg.wedge_chosen_by = wedge_chosen_by;
    tri_dbg.base_wedge = base_wedge_idx;
    tri_dbg.neighbor_wedge = neighbor_wedge_idx;
    tri_dbg.wedge_votes = wedge_votes_map;
    tri_dbg.winner_pct = winner_pct;
    tri_dbg.vote_margin = vote_margin;
    tri_dbg.low_conf_reason = wire_low_conf;
    // Phase 10B debug fields
    tri_dbg.radial_clamp_applied = radial_clamp_applied;
    tri_dbg.radial_clamp_reason = radial_clamp_reason;
    tri_dbg.r_bcwt = r_bcwt_10b;
    tri_dbg.r_bestpair = r_bestpair_10b;
    tri_dbg.radial_delta = radial_delta_10b;
    tri_dbg.near_ring_bcwt = near_ring_bcwt;
    tri_dbg.near_ring_best = near_ring_best;
    tri_dbg.near_ring_any = near_ring_any_10b;
    tri_dbg.x_preclamp_x = x_preclamp.x;
    tri_dbg.x_preclamp_y = x_preclamp.y;
    tri_dbg.x_bestpair_x = x_bestpair_10b.x;
    tri_dbg.x_bestpair_y = x_bestpair_10b.y;
    // Phase 11C CAF debug
    tri_dbg.caf_applied = caf_applied;
    tri_dbg.caf_method = caf_method;
    tri_dbg.theta_bcwt_deg = theta_bcwt_deg_caf;
    tri_dbg.theta_best_deg = theta_best_deg_caf;
    tri_dbg.theta_fused_deg = theta_fused_deg_caf;
    tri_dbg.theta_spread_deg = theta_spread_deg_caf;
    tri_dbg.delta_fused_vs_bcwt_deg = delta_fused_vs_bcwt_caf;
    tri_dbg.wedge_bcwt = wedge_bcwt_caf;
    tri_dbg.wedge_best = wedge_best_caf;
    tri_dbg.wedge_caf = wedge_caf_val;
    tri_dbg.wedge_final = final_score.segment;
    tri_dbg.residual_bcwt_caf = residual_bcwt_caf;
    tri_dbg.residual_caf_val = residual_caf_val;
    tri_dbg.improvement_ratio_caf = improvement_ratio_caf;
    tri_dbg.x_caf_x = x_caf_pt.x;
    tri_dbg.x_caf_y = x_caf_pt.y;
    tri_dbg.caf_effective_cam_count = caf_effective_cam_count;
    tri_dbg.caf_near_boundary = caf_near_boundary;
    tri_dbg.caf_wedge_distance = caf_wedge_distance;
    tri_dbg.caf_soft_accepted = caf_soft_accepted;

    if (caf_applied) {
        result.method = caf_method;
    } else if (radial_clamp_applied) {
        result.method = radial_clamp_method;
    } else {
        result.method = (g_use_bcwt && bcwt_method_used == "BCWT") ? "BCWT" : method;
    }
    result.confidence = confidence;
    result.coords = final_coords;
    result.total_error = best->total_error;
    result.tri_debug = tri_dbg;
    for (const auto& [cam_id, data] : cam_lines)
        result.per_camera[cam_id] = data.vote;
    
    return result;
}

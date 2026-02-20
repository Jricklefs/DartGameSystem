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
    if (std::abs(denom) < 1e-10) return std::nullopt;
    
    double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
    return Point2f(x1 + t * (x2 - x1), y1 + t * (y2 - y1));
}

// ============================================================================
// Line Intersection Triangulation (Autodarts-style)
// ============================================================================

std::optional<IntersectionResult> triangulate_with_line_intersection(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations)
{
    // Step 1: Build TPS transforms and warp lines per camera
    struct CamLine {
        Point2f line_start, line_end;
        Point2f tip_normalized;
        Point2f tip_pixel;
        TpsTransform tps;
        ScoreResult vote;
        bool tip_reliable;
        double tip_dist;
        double mask_quality;
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
        
        // Two points on PCA line in pixel space
        Point2f p1_px(x0 - vx * 200, y0 - vy * 200);
        Point2f p2_px(det.tip->x, det.tip->y);
        
        // Warp to normalized space
        Point2f p1_n = warp_point(tps, p1_px.x, p1_px.y);
        Point2f p2_n = warp_point(tps, p2_px.x, p2_px.y);
        Point2f tip_n = warp_point(tps, det.tip->x, det.tip->y);
        
        // Per-camera vote using ellipse scoring
        ScoreResult vote = score_from_ellipse_calibration(
            det.tip->x, det.tip->y, cal_it->second);
        
        double tip_dist = std::sqrt(tip_n.x * tip_n.x + tip_n.y * tip_n.y);
        bool tip_reliable = tip_dist <= 1.2;
        
        cam_lines[cam_id] = CamLine{
            p1_n, p2_n, tip_n,
            Point2f(det.tip->x, det.tip->y),
            std::move(tps), vote, tip_reliable, tip_dist,
            det.mask_quality
        };
    }
    
    if (cam_lines.size() < 2) return std::nullopt;
    
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
            
            double e1 = std::sqrt(
                (ix->x - l1.tip_normalized.x) * (ix->x - l1.tip_normalized.x) +
                (ix->y - l1.tip_normalized.y) * (ix->y - l1.tip_normalized.y));
            double e2 = std::sqrt(
                (ix->x - l2.tip_normalized.x) * (ix->x - l2.tip_normalized.x) +
                (ix->y - l2.tip_normalized.y) * (ix->y - l2.tip_normalized.y));
            
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
    
    // Step 3: Voting hierarchy
    // Per-camera segment votes
    std::map<std::string, int> cam_votes;
    for (const auto& [cam_id, data] : cam_lines) {
        cam_votes[cam_id] = data.vote.segment;
    }
    
    // Count votes
    std::map<int, int> vote_counts;
    for (const auto& [_, seg] : cam_votes) vote_counts[seg]++;
    
    int most_common_seg = 0, most_common_count = 0;
    for (const auto& [seg, cnt] : vote_counts) {
        if (cnt > most_common_count) {
            most_common_seg = seg;
            most_common_count = cnt;
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
    
    // Miss detection: board-edge override
    if (best->score.multiplier == 2) {
        int edge_count = 0, off_board_count = 0;
        for (const auto& [_, data] : cam_lines) {
            if (data.tip_dist > 0.95) ++edge_count;
            if (data.tip_dist > 1.0) ++off_board_count;
        }
        if (off_board_count >= 1 && edge_count >= 2) {
            // Override to miss
            IntersectionResult result;
            result.segment = 0; result.multiplier = 1; result.score = 0;
            result.method = "MissOverride";
            result.confidence = 0.7;
            result.coords = best->coords;
            result.total_error = best->total_error;
            for (const auto& [cam_id, data] : cam_lines)
                result.per_camera[cam_id] = data.vote;
            return result;
        }
    }
    
    IntersectionResult result;
    result.segment = best->score.segment;
    result.multiplier = best->score.multiplier;
    result.score = best->score.score;
    result.method = method;
    result.confidence = confidence;
    result.coords = best->coords;
    result.total_error = best->total_error;
    for (const auto& [cam_id, data] : cam_lines)
        result.per_camera[cam_id] = data.vote;
    
    return result;
}

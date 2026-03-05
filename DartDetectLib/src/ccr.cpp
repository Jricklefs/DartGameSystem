/**
 * ccr.cpp - Phase 43: Candidate-Constrained Reprojection
 *
 * Post-selection geometric re-evaluation for near-wire ambiguities.
 * When the final score lands near a wedge or ring boundary, CCR tests
 * adjacent regions and picks the one with lowest geometric reprojection cost.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <cmath>
#include <vector>
#include <map>
#include <string>
#include <fstream>
#include <sstream>
#include <chrono>
#include <opencv2/calib3d.hpp>

// ============================================================================
// Feature Flag
// ============================================================================
static bool g_use_ccr = false;

// Thresholds
static constexpr double CCR_ANGULAR_THRESHOLD_DEG = 0.5;
static constexpr double CCR_RADIAL_THRESHOLD = 0.015;
static constexpr double CCR_IMPROVEMENT_RATIO = 0.70;  // must be 30% better

// Safety: track override rate
static int g_ccr_trigger_count = 0;
static int g_ccr_override_count = 0;
static int g_ccr_total_darts = 0;

int set_ccr_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseCCRFlag") { g_use_ccr = (value != 0); return 0; }
    return -1;
}

bool is_ccr_enabled() { return g_use_ccr; }

// ============================================================================
// Board geometry helpers
// ============================================================================

// Ring boundary radii (normalized)
static const double CCR_BULLSEYE_OUTER = 6.35 / 170.0;
static const double CCR_BULL_OUTER     = 16.0 / 170.0;
static const double CCR_TRIPLE_INNER   = 99.0 / 170.0;
static const double CCR_TRIPLE_OUTER   = 107.0 / 170.0;
static const double CCR_DOUBLE_INNER   = 162.0 / 170.0;
static const double CCR_DOUBLE_OUTER   = 1.0;

struct RadialRegion {
    double r_inner;
    double r_outer;
    int multiplier;
    std::string zone;
};

// All radial zones from center out
static const RadialRegion RADIAL_ZONES[] = {
    {0.0,             CCR_BULLSEYE_OUTER, 2, "inner_bull"},
    {CCR_BULLSEYE_OUTER, CCR_BULL_OUTER,  1, "outer_bull"},
    {CCR_BULL_OUTER,  CCR_TRIPLE_INNER,   1, "single_inner"},
    {CCR_TRIPLE_INNER, CCR_TRIPLE_OUTER,  3, "triple"},
    {CCR_TRIPLE_OUTER, CCR_DOUBLE_INNER,  1, "single_outer"},
    {CCR_DOUBLE_INNER, CCR_DOUBLE_OUTER,  2, "double"},
};
static const int NUM_RADIAL_ZONES = 6;

static int find_radial_zone_index(double r) {
    for (int i = 0; i < NUM_RADIAL_ZONES; i++) {
        if (r >= RADIAL_ZONES[i].r_inner && r <= RADIAL_ZONES[i].r_outer)
            return i;
    }
    // Off board
    return -1;
}

// Wedge boundary angle (CW degrees from 12 o'clock) for wedge index i
// Wedge i has boundaries at i*18 - 9 and i*18 + 9
static double wedge_center_angle(int wedge_idx) {
    return wedge_idx * 18.0;
}

static double wedge_lo_angle(int wedge_idx) {
    return wedge_idx * 18.0 - 9.0;
}

static double wedge_hi_angle(int wedge_idx) {
    return wedge_idx * 18.0 + 9.0;
}

// Convert board-space (x,y) to (angle_deg_adjusted, radius)
// angle_deg_adjusted = fmod(atan2(y,-x)*180/pi - 90 + 9 + 360, 360)
static void board_to_polar_adjusted(const Point2f& p, double& adjusted_angle, double& radius) {
    radius = std::sqrt(p.x * p.x + p.y * p.y);
    double angle_rad = std::atan2(p.y, -p.x);
    double angle_deg = angle_rad * 180.0 / CV_PI;
    if (angle_deg < 0) angle_deg += 360.0;
    angle_deg = std::fmod(angle_deg, 360.0);
    adjusted_angle = std::fmod(angle_deg - 90.0 + 9.0 + 360.0, 360.0);
}

// Convert adjusted_angle + radius back to board-space (x,y)
static Point2f polar_adjusted_to_board(double adjusted_angle, double radius) {
    // Reverse: angle_deg = adjusted_angle + 90 - 9
    double angle_deg = adjusted_angle + 90.0 - 9.0;
    if (angle_deg < 0) angle_deg += 360.0;
    angle_deg = std::fmod(angle_deg, 360.0);
    double angle_rad = angle_deg * CV_PI / 180.0;
    // x = -cos(angle_rad)*radius? No: atan2(y, -x) = angle_rad => -x = r*cos(a), y = r*sin(a)
    double x = -radius * std::cos(angle_rad);
    double y = radius * std::sin(angle_rad);
    return Point2f(x, y);
}

// ============================================================================
// Candidate region definition
// ============================================================================
struct CcrRegion {
    double angle_lo;   // adjusted angle, lower bound
    double angle_hi;   // adjusted angle, upper bound
    double r_inner;    // radial inner bound
    double r_outer;    // radial outer bound
    int segment;       // expected segment number
    int multiplier;    // expected multiplier
    std::string label;
};

// Project point to nearest position inside a region
static Point2f project_to_region(const Point2f& p, const CcrRegion& reg) {
    double adj_angle, radius;
    board_to_polar_adjusted(p, adj_angle, radius);

    // Clamp radius
    double r_clamped = std::max(reg.r_inner, std::min(reg.r_outer, radius));

    // Clamp angle (handle wraparound)
    double a_lo = reg.angle_lo;
    double a_hi = reg.angle_hi;

    // Normalize angle relative to region
    double a = adj_angle;
    // Handle wrap: if lo > hi (wraps around 360), adjust
    if (a_lo < a_hi) {
        a = std::max(a_lo, std::min(a_hi, a));
    } else {
        // Wraps around 360
        if (a < a_lo && a > a_hi) {
            // Outside region, pick closer boundary
            double d_lo = std::min(std::abs(a - a_lo), 360.0 - std::abs(a - a_lo));
            double d_hi = std::min(std::abs(a - a_hi), 360.0 - std::abs(a - a_hi));
            a = (d_lo <= d_hi) ? a_lo : a_hi;
        }
    }

    return polar_adjusted_to_board(a, r_clamped);
}

static bool point_in_region(const Point2f& p, const CcrRegion& reg) {
    double adj_angle, radius;
    board_to_polar_adjusted(p, adj_angle, radius);

    if (radius < reg.r_inner || radius > reg.r_outer) return false;

    double a_lo = reg.angle_lo;
    double a_hi = reg.angle_hi;
    if (a_lo < a_hi) {
        return adj_angle >= a_lo && adj_angle <= a_hi;
    } else {
        // Wraps around 360
        return adj_angle >= a_lo || adj_angle <= a_hi;
    }
}

// ============================================================================
// Per-camera warped line data for cost computation
// ============================================================================
struct CcrCamLine {
    Point2f line_point;  // warped tip in board space
    double dir_x, dir_y; // normalized warped direction
    double weight;       // BCWT-style weight
    bool valid;
};

// Perpendicular distance from point to line
static double perp_distance(const Point2f& p, const CcrCamLine& cl) {
    double nx = -cl.dir_y;
    double ny = cl.dir_x;
    double dx = p.x - cl.line_point.x;
    double dy = p.y - cl.line_point.y;
    return std::abs(nx * dx + ny * dy);
}

// Compute weighted reprojection cost at a point
static double compute_cost(const Point2f& p, const std::vector<CcrCamLine>& cam_lines) {
    double cost = 0.0;
    for (const auto& cl : cam_lines) {
        if (!cl.valid) continue;
        double d = perp_distance(p, cl);
        cost += cl.weight * d * d;
    }
    return cost;
}

// ============================================================================
// Logging
// ============================================================================
static void ccr_log(const std::string& msg) {
    static std::ofstream logfile;
    if (!logfile.is_open()) {
        logfile.open("C:\\Users\\clawd\\phase43_ccr_log.txt", std::ios::app);
    }
    if (logfile.is_open()) {
        logfile << msg << std::endl;
        logfile.flush();
    }
}

// ============================================================================
// Main CCR function
// ============================================================================

std::optional<IntersectionResult> ccr_apply(
    const IntersectionResult& result,
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations)
{
    if (!g_use_ccr) return std::nullopt;

    g_ccr_total_darts++;

    // Safety: no bull resolution
    if (result.segment == 25 || result.segment == 0) return std::nullopt;

    // Need tri_debug for boundary distances
    if (!result.tri_debug) return std::nullopt;
    const auto& td = *result.tri_debug;

    // Count valid cameras
    int valid_cams = 0;
    for (const auto& [cid, cd] : td.cam_debug) {
        if (!cd.weak_barrel_signal || cd.barrel_pixel_count >= 40) valid_cams++;
    }
    if (valid_cams < 2) return std::nullopt;

    // Check trigger conditions
    double boundary_deg = td.boundary_distance_deg;
    double radius = td.board_radius;

    // Compute radial boundary distance
    double radial_boundary_dist = 999.0;
    static const double ring_radii[] = {
        CCR_BULLSEYE_OUTER, CCR_BULL_OUTER, CCR_TRIPLE_INNER,
        CCR_TRIPLE_OUTER, CCR_DOUBLE_INNER, CCR_DOUBLE_OUTER
    };
    for (int i = 0; i < 6; i++) {
        radial_boundary_dist = std::min(radial_boundary_dist, std::abs(radius - ring_radii[i]));
    }

    bool angular_ambiguity = (boundary_deg < CCR_ANGULAR_THRESHOLD_DEG);
    bool radial_ambiguity = (radial_boundary_dist < CCR_RADIAL_THRESHOLD);

    if (!angular_ambiguity && !radial_ambiguity) return std::nullopt;

    g_ccr_trigger_count++;

    // Build per-camera warped line data
    std::vector<CcrCamLine> cam_lines;
    for (const auto& [cam_id, det] : camera_results) {
        if (!det.pca_line || !det.tip) continue;
        auto cal_it = calibrations.find(cam_id);
        if (cal_it == calibrations.end()) continue;
        const TpsTransform& tps = cal_it->second.tps_cache;
        if (!tps.valid) continue;

        auto cd_it = td.cam_debug.find(cam_id);
        if (cd_it == td.cam_debug.end()) continue;

        CcrCamLine cl;
        cl.line_point = Point2f(cd_it->second.warped_point_x, cd_it->second.warped_point_y);
        cl.dir_x = cd_it->second.warped_dir_x;
        cl.dir_y = cd_it->second.warped_dir_y;
        cl.weight = std::max(0.1, cd_it->second.detection_quality);
        cl.valid = true;
        cam_lines.push_back(cl);
    }

    if (cam_lines.size() < 2) return std::nullopt;

    // Determine current score info
    Point2f P0 = result.coords;
    double adj_angle, r0;
    board_to_polar_adjusted(P0, adj_angle, r0);
    int current_wedge = ((int)(adj_angle / 18.0)) % 20;
    int current_zone_idx = find_radial_zone_index(r0);

    // Build candidate regions
    std::vector<CcrRegion> candidates;

    // Current region always included
    {
        CcrRegion cur;
        cur.angle_lo = std::fmod(wedge_lo_angle(current_wedge) + 360.0, 360.0);
        cur.angle_hi = std::fmod(wedge_hi_angle(current_wedge) + 360.0, 360.0);
        if (current_zone_idx >= 0) {
            cur.r_inner = RADIAL_ZONES[current_zone_idx].r_inner;
            cur.r_outer = RADIAL_ZONES[current_zone_idx].r_outer;
            cur.multiplier = RADIAL_ZONES[current_zone_idx].multiplier;
        } else {
            cur.r_inner = 0; cur.r_outer = CCR_DOUBLE_OUTER;
            cur.multiplier = result.multiplier;
        }
        cur.segment = result.segment;
        cur.label = "current";
        candidates.push_back(cur);
    }

    if (angular_ambiguity) {
        // CW neighbor
        int cw_wedge = (current_wedge + 1) % 20;
        CcrRegion cw;
        cw.angle_lo = std::fmod(wedge_lo_angle(cw_wedge) + 360.0, 360.0);
        cw.angle_hi = std::fmod(wedge_hi_angle(cw_wedge) + 360.0, 360.0);
        if (current_zone_idx >= 0) {
            cw.r_inner = RADIAL_ZONES[current_zone_idx].r_inner;
            cw.r_outer = RADIAL_ZONES[current_zone_idx].r_outer;
            cw.multiplier = RADIAL_ZONES[current_zone_idx].multiplier;
        } else {
            cw.r_inner = 0; cw.r_outer = CCR_DOUBLE_OUTER;
            cw.multiplier = result.multiplier;
        }
        cw.segment = SEGMENT_ORDER[cw_wedge];
        cw.label = "cw_wedge";
        candidates.push_back(cw);

        // CCW neighbor
        int ccw_wedge = (current_wedge - 1 + 20) % 20;
        CcrRegion ccw;
        ccw.angle_lo = std::fmod(wedge_lo_angle(ccw_wedge) + 360.0, 360.0);
        ccw.angle_hi = std::fmod(wedge_hi_angle(ccw_wedge) + 360.0, 360.0);
        if (current_zone_idx >= 0) {
            ccw.r_inner = RADIAL_ZONES[current_zone_idx].r_inner;
            ccw.r_outer = RADIAL_ZONES[current_zone_idx].r_outer;
            ccw.multiplier = RADIAL_ZONES[current_zone_idx].multiplier;
        } else {
            ccw.r_inner = 0; ccw.r_outer = CCR_DOUBLE_OUTER;
            ccw.multiplier = result.multiplier;
        }
        ccw.segment = SEGMENT_ORDER[ccw_wedge];
        ccw.label = "ccw_wedge";
        candidates.push_back(ccw);
    }

    if (radial_ambiguity && current_zone_idx >= 0) {
        // Inner ring neighbor
        if (current_zone_idx > 0 && current_zone_idx > 1) {  // skip bull zones
            int inner_idx = current_zone_idx - 1;
            if (inner_idx >= 2) {  // only single_inner, triple, single_outer, double
                CcrRegion inner;
                inner.angle_lo = std::fmod(wedge_lo_angle(current_wedge) + 360.0, 360.0);
                inner.angle_hi = std::fmod(wedge_hi_angle(current_wedge) + 360.0, 360.0);
                inner.r_inner = RADIAL_ZONES[inner_idx].r_inner;
                inner.r_outer = RADIAL_ZONES[inner_idx].r_outer;
                inner.multiplier = RADIAL_ZONES[inner_idx].multiplier;
                inner.segment = SEGMENT_ORDER[current_wedge];
                inner.label = "inner_ring";
                candidates.push_back(inner);
            }
        }
        // Outer ring neighbor
        if (current_zone_idx < NUM_RADIAL_ZONES - 1) {
            int outer_idx = current_zone_idx + 1;
            CcrRegion outer;
            outer.angle_lo = std::fmod(wedge_lo_angle(current_wedge) + 360.0, 360.0);
            outer.angle_hi = std::fmod(wedge_hi_angle(current_wedge) + 360.0, 360.0);
            outer.r_inner = RADIAL_ZONES[outer_idx].r_inner;
            outer.r_outer = RADIAL_ZONES[outer_idx].r_outer;
            outer.multiplier = RADIAL_ZONES[outer_idx].multiplier;
            outer.segment = SEGMENT_ORDER[current_wedge];
            outer.label = "outer_ring";
            candidates.push_back(outer);
        }
    }

    if (candidates.size() < 2) return std::nullopt;  // nothing to compare

    // Compute cost for each candidate
    struct CcrCandidate {
        CcrRegion region;
        Point2f eval_point;
        double cost;
    };
    std::vector<CcrCandidate> scored;

    for (const auto& reg : candidates) {
        CcrCandidate cc;
        cc.region = reg;
        if (point_in_region(P0, reg)) {
            cc.eval_point = P0;
        } else {
            cc.eval_point = project_to_region(P0, reg);
        }
        cc.cost = compute_cost(cc.eval_point, cam_lines);
        scored.push_back(cc);
    }

    // Sort by cost ascending
    std::sort(scored.begin(), scored.end(),
        [](const auto& a, const auto& b) { return a.cost < b.cost; });

    const auto& best = scored[0];
    const auto& current = scored[0].region.label == "current" ? scored[0] : scored.back();

    // Find the current region's cost
    double current_cost = scored[0].cost;
    for (const auto& s : scored) {
        if (s.region.label == "current") { current_cost = s.cost; break; }
    }

    // Log
    {
        std::ostringstream log;
        log << "CCR dart#" << g_ccr_total_darts
            << " trigger: angular=" << angular_ambiguity << "(bd=" << boundary_deg << ")"
            << " radial=" << radial_ambiguity << "(rd=" << radial_boundary_dist << ")"
            << " P0=(" << P0.x << "," << P0.y << ")"
            << " seg=" << result.segment << "x" << result.multiplier;
        for (const auto& s : scored) {
            log << " | " << s.region.label << ": cost=" << s.cost
                << " seg=" << s.region.segment << "x" << s.region.multiplier;
        }
        log << " current_cost=" << current_cost;
        ccr_log(log.str());
    }

    // Safety: don't override when current cost is already very low (good fit)
    if (current_cost < 0.001) {
        ccr_log("CCR SKIP: current_cost " + std::to_string(current_cost) + " already very low");
        return std::nullopt;
    }

    // Selection: best must beat current by 30%
    if (best.region.label == "current") return std::nullopt;  // current is already best
    if (best.cost >= current_cost * CCR_IMPROVEMENT_RATIO) return std::nullopt;  // not enough improvement

    // Safety: no jumps > 1 wedge
    // The candidates are already constrained to +-1 wedge and +-1 ring by construction

    // Safety: hard cap on override rate (10%)
    if (g_ccr_total_darts > 20) {
        double override_rate = (double)(g_ccr_override_count + 1) / g_ccr_total_darts;
        if (override_rate > 0.10) {
            ccr_log("CCR BLOCKED: override rate would exceed 10%");
            return std::nullopt;
        }
    }

    g_ccr_override_count++;

    // Build override result
    IntersectionResult override_result = result;
    override_result.segment = best.region.segment;
    override_result.multiplier = best.region.multiplier;
    override_result.score = best.region.segment * best.region.multiplier;
    override_result.method = result.method + "+CCR_" + best.region.label;

    // Use the eval point if it's different from P0 (projected into region)
    if (!point_in_region(P0, best.region)) {
        override_result.coords = best.eval_point;
    }

    // Add CCR debug to tri_debug
    if (override_result.tri_debug) {
        auto& otd = *override_result.tri_debug;
        otd.ccr_applied = true;
        otd.ccr_selected_label = best.region.label;
        otd.ccr_current_cost = current_cost;
        otd.ccr_best_cost = best.cost;
        otd.ccr_candidate_count = (int)candidates.size();
        otd.ccr_angular_ambiguity = angular_ambiguity;
        otd.ccr_radial_ambiguity = radial_ambiguity;
    }

    ccr_log("CCR OVERRIDE: " + best.region.label + " seg=" +
            std::to_string(best.region.segment) + "x" +
            std::to_string(best.region.multiplier) +
            " cost=" + std::to_string(best.cost) + " vs " +
            std::to_string(current_cost));

    return override_result;
}

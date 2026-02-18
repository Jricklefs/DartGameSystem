/**
 * scoring.cpp - Segment scoring from polar/ellipse coordinates + voting
 * 
 * Ported from Python: routes.py score_from_polar(), score_from_ellipse_calibration(),
 * ellipse_scoring.py
 */
#include "dart_detect_internal.h"
#include <cmath>
#include <algorithm>
#include <map>

// ============================================================================
// Ellipse radius at angle (polar form)
// ============================================================================

double ellipse_radius_at_angle(const EllipseData& ellipse, double angle_rad)
{
    double a = ellipse.width / 2.0;   // semi-major
    double b = ellipse.height / 2.0;  // semi-minor
    double rot_rad = ellipse.rotation_deg * CV_PI / 180.0;
    
    // Angle relative to ellipse axes
    double theta = angle_rad - rot_rad;
    
    double cos_t = std::cos(theta);
    double sin_t = std::sin(theta);
    double denom = std::sqrt((b * cos_t) * (b * cos_t) + (a * sin_t) * (a * sin_t));
    if (denom < 1e-6) return 0.0;
    return (a * b) / denom;
}

// ============================================================================
// Score from polar coordinates (normalized board space)
// ============================================================================

ScoreResult score_from_polar(double angle_deg, double norm_dist)
{
    ScoreResult r;
    
    // Bull zones
    if (norm_dist <= BULLSEYE_NORM) {
        r.segment = 25; r.multiplier = 2; r.score = 50;
        r.zone = "inner_bull"; r.boundary_distance_deg = 9.0;
        return r;
    }
    if (norm_dist <= OUTER_BULL_NORM) {
        r.segment = 0; r.multiplier = 1; r.score = 25;
        r.zone = "outer_bull"; r.boundary_distance_deg = 9.0;
        return r;
    }
    if (norm_dist > DOUBLE_OUTER_NORM * 1.05) {
        r.segment = 0; r.multiplier = 0; r.score = 0;
        r.zone = "miss"; r.boundary_distance_deg = 0.0;
        return r;
    }
    
    // Segment from angle
    // 20 is at top (90┬░), each segment is 18┬░ wide
    double adjusted_angle = std::fmod(angle_deg - 90.0 + 9.0 + 360.0, 360.0);
    int segment_idx = ((int)(adjusted_angle / 18.0)) % 20;
    r.segment = SEGMENT_ORDER[segment_idx];
    
    // Boundary distance
    double angle_in_segment = std::fmod(adjusted_angle, 18.0);
    r.boundary_distance_deg = std::min(angle_in_segment, 18.0 - angle_in_segment);
    
    // Multiplier from distance
    if (norm_dist >= DOUBLE_INNER_NORM && norm_dist <= DOUBLE_OUTER_NORM * 1.05) {
        r.multiplier = 2; r.zone = "double";
    } else if (norm_dist >= TRIPLE_INNER_NORM && norm_dist <= TRIPLE_OUTER_NORM) {
        r.multiplier = 3; r.zone = "triple";
    } else if (norm_dist < TRIPLE_INNER_NORM) {
        r.multiplier = 1; r.zone = "single_inner";
    } else {
        r.multiplier = 1; r.zone = "single_outer";
    }
    
    r.score = r.segment * r.multiplier;
    return r;
}

// ============================================================================
// Score from ellipse calibration (per-camera pixel space)
// ============================================================================

ScoreResult score_from_ellipse_calibration(
    double tip_x, double tip_y,
    const CameraCalibration& cal)
{
    ScoreResult r;
    
    double dx = tip_x - cal.center.x;
    double dy = tip_y - cal.center.y;
    double dist = std::sqrt(dx * dx + dy * dy);
    double angle = std::atan2(dy, dx);
    
    // Check each ring (from inside out)
    // Bullseye
    if (cal.bullseye_ellipse) {
        double bull_r = ellipse_radius_at_angle(*cal.bullseye_ellipse, angle);
        if (dist <= bull_r) {
            r.segment = 25; r.multiplier = 2; r.score = 50;
            r.zone = "inner_bull"; r.boundary_distance_deg = 9.0;
            return r;
        }
    }
    
    // Outer bull
    if (cal.bull_ellipse) {
        double bull_r = ellipse_radius_at_angle(*cal.bull_ellipse, angle);
        if (dist <= bull_r) {
            r.segment = 0; r.multiplier = 1; r.score = 25;
            r.zone = "outer_bull"; r.boundary_distance_deg = 9.0;
            return r;
        }
    }
    
    // Determine segment from angle using calibration's segment_angles
    if (cal.segment_angles.size() >= 20) {
        int seg20_idx = cal.segment_20_index;
        
        // Find which angular wedge the tip falls in
        int found_idx = -1;
        for (int i = 0; i < 20; ++i) {
            double a1 = cal.segment_angles[i];
            double a2 = cal.segment_angles[(i + 1) % 20];
            
            // Normalize angles for comparison
            double tip_angle = angle;
            
            // Handle wraparound
            auto angle_between = [](double a, double lo, double hi) -> bool {
                // Normalize all to [0, 2╧Ç)
                auto norm = [](double x) -> double {
                    while (x < 0) x += 2 * CV_PI;
                    while (x >= 2 * CV_PI) x -= 2 * CV_PI;
                    return x;
                };
                a = norm(a); lo = norm(lo); hi = norm(hi);
                if (lo <= hi) return a >= lo && a < hi;
                return a >= lo || a < hi;  // Wraps around 0/2╧Ç
            };
            
            if (angle_between(tip_angle, a1, a2)) {
                found_idx = i;
                break;
            }
        }
        
        if (found_idx >= 0) {
            int board_idx = ((found_idx - seg20_idx) % 20 + 20) % 20;
            r.segment = SEGMENT_ORDER[board_idx];
            
            // Boundary distance (approximate)
            double a1 = cal.segment_angles[found_idx];
            double a2 = cal.segment_angles[(found_idx + 1) % 20];
            double diff1 = std::abs(angle - a1);
            if (diff1 > CV_PI) diff1 = 2 * CV_PI - diff1;
            double diff2 = std::abs(angle - a2);
            if (diff2 > CV_PI) diff2 = 2 * CV_PI - diff2;
            r.boundary_distance_deg = std::min(diff1, diff2) * 180.0 / CV_PI;
        }
    }
    
    // Determine multiplier from ring distances
    bool in_triple = false, in_double = false;
    
    if (cal.inner_triple_ellipse && cal.outer_triple_ellipse) {
        double r_inner = ellipse_radius_at_angle(*cal.inner_triple_ellipse, angle);
        double r_outer = ellipse_radius_at_angle(*cal.outer_triple_ellipse, angle);
        if (dist >= r_inner && dist <= r_outer) in_triple = true;
    }
    
    if (cal.inner_double_ellipse && cal.outer_double_ellipse) {
        double r_inner = ellipse_radius_at_angle(*cal.inner_double_ellipse, angle);
        double r_outer = ellipse_radius_at_angle(*cal.outer_double_ellipse, angle);
        if (dist >= r_inner && dist <= r_outer) in_double = true;
    }
    
    // Check if outside board
    if (cal.outer_double_ellipse) {
        double r_outer = ellipse_radius_at_angle(*cal.outer_double_ellipse, angle);
        if (dist > r_outer * 1.05) {
            r.segment = 0; r.multiplier = 0; r.score = 0;
            r.zone = "miss";
            return r;
        }
    }
    
    if (in_triple) {
        r.multiplier = 3; r.zone = "triple";
    } else if (in_double) {
        r.multiplier = 2; r.zone = "double";
    } else {
        r.multiplier = 1;
        // Determine inner vs outer single
        if (cal.inner_triple_ellipse) {
            double r_triple_inner = ellipse_radius_at_angle(*cal.inner_triple_ellipse, angle);
            r.zone = (dist < r_triple_inner) ? "single_inner" : "single_outer";
        } else {
            r.zone = "single";
        }
    }
    
    r.score = r.segment * r.multiplier;
    r.confidence = 0.8;
    return r;
}

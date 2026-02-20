#!/usr/bin/env python3
"""Patch script to precompute TPS at init and add 40-angle support."""
import re

# === 1. Patch dart_detect_internal.h: Add tps_cache to CameraCalibration ===
with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\include\dart_detect_internal.h', 'r') as f:
    h = f.read()

old = '''    std::optional<EllipseData> bullseye_ellipse;
};'''
new = '''    std::optional<EllipseData> bullseye_ellipse;
    
    // Precomputed TPS transform (built once at init, not per-detection)
    TpsTransform tps_cache;
};'''
assert old in h, "Could not find CameraCalibration closing in header"
h = h.replace(old, new)

with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\include\dart_detect_internal.h', 'w') as f:
    f.write(h)
print("Patched dart_detect_internal.h")

# === 2. Patch dart_detect.cpp: Precompute TPS in dd_init() ===
with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\src\dart_detect.cpp', 'r') as f:
    c = f.read()

old2 = '''    if (!parse_calibrations(json, g_calibrations)) {
        return -1;
    }
    
    g_initialized = true;'''
new2 = '''    if (!parse_calibrations(json, g_calibrations)) {
        return -1;
    }
    
    // Precompute TPS transforms for each camera (expensive, do once)
    for (auto& [cam_id, cal] : g_calibrations) {
        cal.tps_cache = build_tps_transform(cal);
    }
    
    g_initialized = true;'''
assert old2 in c, "Could not find dd_init parse section"
c = c.replace(old2, new2)

with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\src\dart_detect.cpp', 'w') as f:
    f.write(c)
print("Patched dart_detect.cpp")

# === 3. Patch triangulation.cpp: Use cached TPS instead of rebuilding ===
with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\src\triangulation.cpp', 'r') as f:
    t = f.read()

old3 = '''        TpsTransform tps = build_tps_transform(cal_it->second);
        if (!tps.valid) continue;'''
new3 = '''        const TpsTransform& tps = cal_it->second.tps_cache;
        if (!tps.valid) continue;'''
assert old3 in t, "Could not find TPS build call in triangulation"
t = t.replace(old3, new3)

# === 4. Add midpoint angles to build_tps_transform for 40-angle TPS ===
# After the main ring loop and before the mid-ring interpolation, add midpoint angle sampling
old4 = '''    // Add mid-ring interpolated control points for smoother TPS in gap regions'''
new4 = '''    // Add midpoint angles (between boundary angles) for each ring - 40 total angles per ring
    for (const auto& ring : rings) {
        if (!ring.ellipse->has_value()) continue;
        const auto& ell = ring.ellipse->value();
        
        for (int idx = 0; idx < 20; ++idx) {
            int next_idx = (idx + 1) % 20;
            // Midpoint angle between two boundary angles
            double a1 = cal.segment_angles[idx];
            double a2 = cal.segment_angles[next_idx];
            // Handle wrap-around
            double diff = a2 - a1;
            if (diff > CV_PI) diff -= 2 * CV_PI;
            if (diff < -CV_PI) diff += 2 * CV_PI;
            double mid_angle = a1 + diff / 2.0;
            
            auto px_pt = sample_ellipse_at_angle(ell, mid_angle, bcx, bcy);
            if (!px_pt) continue;
            
            src_x.push_back(px_pt->x);
            src_y.push_back(px_pt->y);
            
            // Board-space: midpoint is at the center of the segment
            int board_idx = ((idx - seg20_idx) % 20 + 20) % 20;
            double angle_cw_deg = board_idx * 18.0;  // center of segment (boundary is at -9, next at +9, mid at 0)
            double angle_cw_rad = angle_cw_deg * CV_PI / 180.0;
            dst_x.push_back(ring.norm_radius * std::sin(angle_cw_rad));
            dst_y.push_back(ring.norm_radius * std::cos(angle_cw_rad));
        }
    }
    
    // Add mid-ring interpolated control points for smoother TPS in gap regions'''
assert old4 in t, "Could not find mid-ring comment"
t = t.replace(old4, new4)

# Also add midpoint angles for mid-ring interpolated points
old5 = '''    // Add center anchor'''
new5 = '''    // Add midpoint angles for mid-ring interpolated control points
    for (const auto& mr : mid_rings) {
        if (!mr.inner_ell->has_value() || !mr.outer_ell->has_value()) continue;
        const auto& ell_in = mr.inner_ell->value();
        const auto& ell_out = mr.outer_ell->value();
        for (int idx = 0; idx < 20; ++idx) {
            int next_idx = (idx + 1) % 20;
            double a1 = cal.segment_angles[idx];
            double a2 = cal.segment_angles[next_idx];
            double diff = a2 - a1;
            if (diff > CV_PI) diff -= 2 * CV_PI;
            if (diff < -CV_PI) diff += 2 * CV_PI;
            double mid_angle = a1 + diff / 2.0;
            
            auto pt_in = sample_ellipse_at_angle(ell_in, mid_angle, bcx, bcy);
            auto pt_out = sample_ellipse_at_angle(ell_out, mid_angle, bcx, bcy);
            if (!pt_in || !pt_out) continue;
            src_x.push_back((pt_in->x + pt_out->x) / 2.0);
            src_y.push_back((pt_in->y + pt_out->y) / 2.0);
            int board_idx = ((idx - seg20_idx) % 20 + 20) % 20;
            double angle_cw_deg = board_idx * 18.0;
            double angle_cw_rad = angle_cw_deg * CV_PI / 180.0;
            dst_x.push_back(mr.norm_radius * std::sin(angle_cw_rad));
            dst_y.push_back(mr.norm_radius * std::cos(angle_cw_rad));
        }
    }
    
    // Add center anchor'''
assert old5 in t, "Could not find center anchor comment"
t = t.replace(old5, new5)

with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\src\triangulation.cpp', 'w') as f:
    f.write(t)
print("Patched triangulation.cpp")

print("\nAll patches applied successfully!")

#!/usr/bin/env python3
"""Fix: move TpsTransform struct before CameraCalibration."""
with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\include\dart_detect_internal.h', 'r') as f:
    h = f.read()

# Extract TpsTransform definition
tps_block = '''// TPS (Thin-Plate Spline) transform data
struct TpsTransform {
    cv::Mat src_points;  // Nx2 source (pixel) control points
    cv::Mat dst_points;  // Nx2 destination (normalized board) control points
    cv::Mat weights;     // TPS weights (Nx2 + 3x2)
    bool valid = false;
    
    // Transform a point from pixel to normalized board space
    Point2f transform(double px, double py) const;
};'''

# Remove it from its current location
assert tps_block in h
h = h.replace(tps_block + '\n\n', '')  # remove with trailing newlines

# Insert before CameraCalibration
insert_before = 'struct CameraCalibration {'
assert insert_before in h
h = h.replace(insert_before, tps_block + '\n\n' + insert_before)

with open(r'C:\Users\clawd\DartGameSystem\DartDetectLib\include\dart_detect_internal.h', 'w') as f:
    f.write(h)
print("Fixed struct ordering")

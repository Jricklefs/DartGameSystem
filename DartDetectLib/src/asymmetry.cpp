/**
 * asymmetry.cpp - Wire boundary barrel edge asymmetry detector
 * 
 * When a dart lands near a wire boundary, the wire occludes part of the barrel
 * from certain camera angles. This creates asymmetric barrel edges in the diff:
 * - Occluded side: sharp gradient dropoff (~50 intensity in 1-2px)
 * - Non-occluded side: gradual taper (~15 intensity over 3-4px)
 * 
 * The asymmetry ratio tells us which side the wire is on relative to the barrel.
 */
#include "dart_detect_internal.h"
#include <cmath>
#include <cstdio>
#include <algorithm>
#include <vector>

AsymmetryResult analyze_barrel_asymmetry(
    const cv::Mat& grayscale_diff,
    const Point2f& tip,
    const PcaLine& barrel_line,
    int sample_radius)
{
    AsymmetryResult result;
    
    {
        FILE* f = fopen("C:\\Users\\clawd\\asym_diag.log", "a");
        if (f) {
            fprintf(f, "[ASYM_FUNC] called diff=%dx%d tip=(%.1f,%.1f) barrel_vx=%.3f vy=%.3f\n",
                    grayscale_diff.cols, grayscale_diff.rows, tip.x, tip.y, barrel_line.vx, barrel_line.vy);
            fclose(f);
        }
    }
    
    if (grayscale_diff.empty()) return result;
    
    int h = grayscale_diff.rows, w = grayscale_diff.cols;
    
    // Perpendicular direction to barrel line
    double perp_x = -barrel_line.vy;
    double perp_y = barrel_line.vx;
    
    // Sample intensity along perpendicular line at tip position
    // Also sample at a few points along the barrel near the tip for robustness
    std::vector<double> left_gradients, right_gradients;
    
    // Sample at 3 positions along barrel near tip: tip, tip-5px, tip-10px (back from tip)
    for (int offset = 0; offset <= 10; offset += 5) {
        double cx = tip.x - barrel_line.vx * offset;
        double cy = tip.y - barrel_line.vy * offset;
        
        // Sample intensity values on each side of center
        std::vector<double> left_vals, right_vals;
        
        for (int t = 0; t <= sample_radius; ++t) {
            // Left side (negative perp direction)
            int lx = (int)std::round(cx - perp_x * t);
            int ly = (int)std::round(cy - perp_y * t);
            if (lx >= 0 && lx < w && ly >= 0 && ly < h)
                left_vals.push_back((double)grayscale_diff.at<uchar>(ly, lx));
            else
                left_vals.push_back(0.0);
            
            // Right side (positive perp direction)
            int rx = (int)std::round(cx + perp_x * t);
            int ry = (int)std::round(cy + perp_y * t);
            if (rx >= 0 && rx < w && ry >= 0 && ry < h)
                right_vals.push_back((double)grayscale_diff.at<uchar>(ry, rx));
            else
                right_vals.push_back(0.0);
        }
        
        // Compute gradient magnitude for each side
        // Gradient = max intensity drop over a sliding window of 3px
        auto compute_edge_gradient = [](const std::vector<double>& vals) -> double {
            if (vals.size() < 4) return 0.0;
            double max_grad = 0.0;
            // Find the peak intensity (should be near center = barrel)
            double peak = 0.0;
            int peak_idx = 0;
            for (int i = 0; i < (int)vals.size() && i < 8; ++i) {
                if (vals[i] > peak) { peak = vals[i]; peak_idx = i; }
            }
            // Compute steepest drop from peak outward
            for (int i = peak_idx; i < (int)vals.size() - 1; ++i) {
                double drop = vals[i] - vals[i+1];
                if (drop > max_grad) max_grad = drop;
            }
            // Also compute average gradient over 3px window
            if (peak_idx + 3 < (int)vals.size()) {
                double avg_drop = (vals[peak_idx] - vals[std::min(peak_idx + 3, (int)vals.size()-1)]) / 3.0;
                max_grad = std::max(max_grad, avg_drop);
            }
            return max_grad;
        };
        
        double left_grad = compute_edge_gradient(left_vals);
        double right_grad = compute_edge_gradient(right_vals);
        
        if (left_grad > 2.0) left_gradients.push_back(left_grad);
        if (right_grad > 2.0) right_gradients.push_back(right_grad);
    }
    
    if (left_gradients.empty() || right_gradients.empty()) return result;
    
    // Average gradients across sample positions
    double avg_left = 0, avg_right = 0;
    for (double g : left_gradients) avg_left += g;
    for (double g : right_gradients) avg_right += g;
    avg_left /= left_gradients.size();
    avg_right /= right_gradients.size();
    
    double max_grad = std::max(avg_left, avg_right);
    double min_grad = std::min(avg_left, avg_right);
    
    if (min_grad < 1.0) min_grad = 1.0;  // prevent division by zero
    
    result.asymmetry_ratio = max_grad / min_grad;
    
    // Determine which side is steep (wire side)
    // Steep side = wire occlusion = wire is on that side
    if (avg_left > avg_right) {
        // Wire is on the left (negative perp direction)
        result.steep_side_angle = std::atan2(-perp_y, -perp_x);
    } else {
        // Wire is on the right (positive perp direction)
        result.steep_side_angle = std::atan2(perp_y, perp_x);
    }
    
    {
        FILE* f = fopen("C:\\Users\\clawd\\asym_diag.log", "a");
        if (f) {
            fprintf(f, "[ASYM_RESULT] ratio=%.2f steep_angle=%.3f\n", result.asymmetry_ratio, result.steep_side_angle);
            fclose(f);
        }
    }
    
    if (result.asymmetry_ratio > 2.0) {
        result.wire_side_determined = true;
        result.confidence = std::min(1.0, (result.asymmetry_ratio - 2.0) / 4.0);
    }
    
    return result;
}

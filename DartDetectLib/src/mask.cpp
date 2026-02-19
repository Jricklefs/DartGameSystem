/**
 * mask.cpp - Motion mask computation & pixel segmentation
 * 
 * Ported from Python: skeleton_detection.py _compute_motion_mask(),
 * _compute_pixel_segmentation(), _shape_filter()
 */
#include "dart_detect_internal.h"

// ============================================================================
// Motion Mask - Hysteresis thresholding with morphological cleanup
// ============================================================================

MotionMaskResult compute_motion_mask(
    const cv::Mat& current,
    const cv::Mat& previous,
    int blur_size,
    int threshold)
{
    MotionMaskResult result;
    
    // Convert to grayscale
    cv::Mat gray_curr, gray_prev;
    if (current.channels() == 3) {
        cv::cvtColor(current, gray_curr, cv::COLOR_BGR2GRAY);
    } else {
        gray_curr = current;
    }
    if (previous.channels() == 3) {
        cv::cvtColor(previous, gray_prev, cv::COLOR_BGR2GRAY);
    } else {
        gray_prev = previous;
    }
    
    // Gaussian blur
    cv::Mat blur_curr, blur_prev;
    cv::GaussianBlur(gray_curr, blur_curr, cv::Size(blur_size, blur_size), 0);
    cv::GaussianBlur(gray_prev, blur_prev, cv::Size(blur_size, blur_size), 0);
    
    // Absolute difference
    cv::Mat diff;
    cv::absdiff(blur_curr, blur_prev, diff);
    
    // Multi-threshold hysteresis
    cv::Mat mask_high, mask_low;
    cv::threshold(diff, mask_high, threshold, 255, cv::THRESH_BINARY);
    cv::threshold(diff, mask_low, std::max(5, threshold / 3), 255, cv::THRESH_BINARY);
    
    // Aggressive close on low mask to bridge flight-shaft gaps
    cv::Mat close_kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::morphologyEx(mask_low, mask_low, cv::MORPH_CLOSE, close_kernel);
    
    // Hysteresis: grow high-threshold seeds into connected low-threshold pixels
    cv::Mat seed = mask_high.clone();
    cv::Mat dilate_kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    for (int iter = 0; iter < 50; ++iter) {
        cv::Mat expanded;
        cv::dilate(seed, expanded, dilate_kernel, cv::Point(-1, -1), 1);
        cv::Mat new_pixels;
        cv::bitwise_and(expanded, mask_low, new_pixels);
        if (cv::countNonZero(new_pixels != seed) == 0) break;
        // More precise check: if no change
        cv::Mat diff_check;
        cv::compare(new_pixels, seed, diff_check, cv::CMP_NE);
        if (cv::countNonZero(diff_check) == 0) break;
        seed = new_pixels;
    }
    
    // Morphological opening to trim noise
    cv::Mat open_kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::morphologyEx(seed, seed, cv::MORPH_OPEN, open_kernel);
    
    result.mask = seed;
    result.high_mask = mask_high;
    
    // Positive mask: pixels that appeared (signed diff > threshold)
    cv::Mat signed_diff;
    blur_curr.convertTo(signed_diff, CV_16S);
    cv::Mat prev16;
    blur_prev.convertTo(prev16, CV_16S);
    signed_diff -= prev16;
    
    result.positive_mask = cv::Mat::zeros(mask_high.size(), CV_8U);
    for (int r = 0; r < signed_diff.rows; ++r) {
        const short* row = signed_diff.ptr<short>(r);
        uchar* out = result.positive_mask.ptr<uchar>(r);
        for (int c = 0; c < signed_diff.cols; ++c) {
            if (row[c] > threshold) out[c] = 255;
        }
    }
    
    return result;
}

// ============================================================================
// Pixel Segmentation - Autodarts-style 4-category classification
// ============================================================================

PixelSegmentation compute_pixel_segmentation(
    const cv::Mat& current,
    const cv::Mat& previous,
    const std::vector<cv::Mat>& prev_dart_masks,
    int threshold,
    int blur_size)
{
    PixelSegmentation seg;
    
    // Grayscale + blur
    cv::Mat gray_curr, gray_prev, blur_curr, blur_prev;
    if (current.channels() == 3)
        cv::cvtColor(current, gray_curr, cv::COLOR_BGR2GRAY);
    else gray_curr = current;
    if (previous.channels() == 3)
        cv::cvtColor(previous, gray_prev, cv::COLOR_BGR2GRAY);
    else gray_prev = previous;
    
    cv::GaussianBlur(gray_curr, blur_curr, cv::Size(blur_size, blur_size), 0);
    cv::GaussianBlur(gray_prev, blur_prev, cv::Size(blur_size, blur_size), 0);
    
    // Signed difference
    cv::Mat signed_diff;
    blur_curr.convertTo(signed_diff, CV_16S);
    cv::Mat prev16;
    blur_prev.convertTo(prev16, CV_16S);
    signed_diff -= prev16;
    
    // Compute full motion mask with hysteresis (same as compute_motion_mask)
    cv::Mat diff;
    cv::absdiff(blur_curr, blur_prev, diff);
    cv::Mat mask_high, mask_low;
    cv::threshold(diff, mask_high, threshold, 255, cv::THRESH_BINARY);
    cv::threshold(diff, mask_low, std::max(5, threshold / 3), 255, cv::THRESH_BINARY);
    
    cv::Mat close_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::morphologyEx(mask_low, mask_low, cv::MORPH_CLOSE, close_kern);
    
    cv::Mat seed = mask_high.clone();
    cv::Mat dil_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    for (int i = 0; i < 50; ++i) {
        cv::Mat expanded, new_pixels;
        cv::dilate(seed, expanded, dil_kern);
        cv::bitwise_and(expanded, mask_low, new_pixels);
        cv::Mat diff_check;
        cv::compare(new_pixels, seed, diff_check, cv::CMP_NE);
        if (cv::countNonZero(diff_check) == 0) break;
        seed = new_pixels;
    }
    
    cv::Mat open_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::morphologyEx(seed, seg.full_motion_mask, cv::MORPH_OPEN, open_kern);
    
    int h = seg.full_motion_mask.rows;
    int w = seg.full_motion_mask.cols;
    
    // Classify: appeared vs disappeared
    cv::Mat appeared = cv::Mat::zeros(h, w, CV_8U);
    cv::Mat disappeared = cv::Mat::zeros(h, w, CV_8U);
    
    for (int r = 0; r < h; ++r) {
        const short* sd = signed_diff.ptr<short>(r);
        const uchar* fm = seg.full_motion_mask.ptr<uchar>(r);
        uchar* app = appeared.ptr<uchar>(r);
        uchar* dis = disappeared.ptr<uchar>(r);
        for (int c = 0; c < w; ++c) {
            if (fm[c] == 0) continue;
            if (sd[c] > threshold) app[c] = 255;
            if (sd[c] < -threshold) dis[c] = 255;
        }
    }
    
    if (prev_dart_masks.empty()) {
        // Dart 1: all appeared pixels are new
        seg.new_mask = appeared.clone();
        seg.old_mask = disappeared.clone();
        seg.moved_mask = cv::Mat::zeros(h, w, CV_8U);
        seg.stationary_mask = cv::Mat::zeros(h, w, CV_8U);
    } else {
        // Dart 2+: classify against previous masks
        cv::Mat combined_prev = cv::Mat::zeros(h, w, CV_8U);
        for (const auto& pm : prev_dart_masks) {
            if (!pm.empty() && pm.rows == h && pm.cols == w)
                cv::bitwise_or(combined_prev, pm, combined_prev);
        }
        
        // Stationary: appeared AND in previous masks
        cv::bitwise_and(appeared, combined_prev, seg.stationary_mask);
        
        // Old: disappeared AND in previous masks
        cv::bitwise_and(disappeared, combined_prev, seg.old_mask);
        
        // Moved: appeared pixels near old/prev pixels
        seg.moved_mask = cv::Mat::zeros(h, w, CV_8U);
        if (cv::countNonZero(seg.old_mask) > 0) {
            int ksize = MOVED_PIXEL_DISTANCE * 2 + 1;
            cv::Mat move_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(ksize, ksize));
            cv::Mat old_vicinity;
            cv::dilate(seg.old_mask, old_vicinity, move_kern);
            
            int pksize = MOVED_PIXEL_DISTANCE + 1;
            cv::Mat prev_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(pksize, pksize));
            cv::Mat prev_vicinity;
            cv::dilate(combined_prev, prev_vicinity, prev_kern);
            
            cv::Mat vicinity;
            cv::bitwise_or(old_vicinity, prev_vicinity, vicinity);
            
            cv::Mat candidate_moved, not_stat;
            cv::bitwise_and(appeared, vicinity, candidate_moved);
            cv::bitwise_not(seg.stationary_mask, not_stat);
            cv::bitwise_and(candidate_moved, not_stat, seg.moved_mask);
        }
        
        // New: appeared minus stationary minus moved
        cv::Mat not_stat, not_moved;
        cv::bitwise_not(seg.stationary_mask, not_stat);
        cv::bitwise_not(seg.moved_mask, not_moved);
        cv::bitwise_and(appeared, not_stat, seg.new_mask);
        cv::bitwise_and(seg.new_mask, not_moved, seg.new_mask);
        
        // Uncategorized motion pixels not in prev masks -> new
        cv::Mat uncategorized = seg.full_motion_mask.clone();
        cv::Mat not_old, not_new, not_prev;
        cv::bitwise_not(seg.old_mask, not_old);
        cv::bitwise_not(seg.new_mask, not_new);
        cv::bitwise_not(combined_prev, not_prev);
        cv::bitwise_and(uncategorized, not_stat, uncategorized);
        cv::bitwise_and(uncategorized, not_moved, uncategorized);
        cv::bitwise_and(uncategorized, not_old, uncategorized);
        cv::bitwise_and(uncategorized, not_new, uncategorized);
        cv::Mat uncategorized_new;
        cv::bitwise_and(uncategorized, not_prev, uncategorized_new);
        cv::bitwise_or(seg.new_mask, uncategorized_new, seg.new_mask);
    }
    
    seg.new_count = cv::countNonZero(seg.new_mask);
    seg.old_count = cv::countNonZero(seg.old_mask);
    seg.moved_count = cv::countNonZero(seg.moved_mask);
    seg.stationary_count = cv::countNonZero(seg.stationary_mask);
    int total = seg.new_count + seg.old_count + seg.moved_count + seg.stationary_count;
    seg.new_dart_pixel_ratio = (total > 0) ? (double)seg.new_count / total : 0.0;
    
    return seg;
}

// ============================================================================
// Shape Filter - Keep only elongated (dart-shaped) blobs
// ============================================================================

cv::Mat shape_filter(const cv::Mat& mask, double min_aspect, int min_area)
{
    cv::Mat labels, stats, centroids;
    int num_labels = cv::connectedComponentsWithStats(mask, labels, stats, centroids);
    
    cv::Mat filtered = cv::Mat::zeros(mask.size(), CV_8U);
    
    for (int id = 1; id < num_labels; ++id) {
        int area = stats.at<int>(id, cv::CC_STAT_AREA);
        if (area < min_area) continue;
        
        // Get component pixels
        cv::Mat component = (labels == id);
        component.convertTo(component, CV_8U, 255);
        
        std::vector<cv::Point> points;
        cv::findNonZero(component, points);
        if ((int)points.size() < 5) continue;
        
        // Fit oriented bounding box
        cv::RotatedRect rect = cv::minAreaRect(points);
        double long_side = std::max(rect.size.width, rect.size.height);
        double short_side = std::min(rect.size.width, rect.size.height) + 1.0;
        double aspect = long_side / short_side;
        
        // Keep if elongated or large enough
        if (aspect >= min_aspect) {
            cv::bitwise_or(filtered, component, filtered);
        } else if (area >= 2000 && aspect >= 1.3) {
            cv::bitwise_or(filtered, component, filtered);
        }
    }
    
    return filtered;
}

/**
 * sghf.cpp - Phase 23: Specular/Glare Handling + HDR Fusion
 *
 * Reduces specular glare in diff images via soft-knee compression,
 * CLAHE local contrast enhancement, and multi-exposure fusion
 * before feeding into IQDL (Phase 17).
 *
 * Does NOT modify IQDL logic — only transforms the diff image input.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <numeric>
#include <cmath>
#include <iostream>

// ============================================================================
// Feature Flags (default OFF; sub-flags default ON when SGHF is active)
// ============================================================================
static bool g_use_sghf = false;
static bool g_sghf_enable_specular_clamp = true;
static bool g_sghf_enable_local_contrast = true;
static bool g_sghf_enable_multi_exposure_fusion = true;
static bool g_sghf_fallback_to_legacy_diff = true;

int set_sghf_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseSGHF") { g_use_sghf = (value != 0); return 0; }
    if (s == "SGHF_EnableSpecularClamp") { g_sghf_enable_specular_clamp = (value != 0); return 0; }
    if (s == "SGHF_EnableLocalContrast") { g_sghf_enable_local_contrast = (value != 0); return 0; }
    if (s == "SGHF_EnableMultiExposureFusion") { g_sghf_enable_multi_exposure_fusion = (value != 0); return 0; }
    if (s == "SGHF_FallbackToLegacyDiff") { g_sghf_fallback_to_legacy_diff = (value != 0); return 0; }
    return -1;
}

bool sghf_is_enabled() { return g_use_sghf; }

// ============================================================================
// Parameters
// ============================================================================
static const double EPS = 1e-6;

// Specular clamp
static const double SPEC_CLIP_PERCENTILE = 99.2;
static const double SPEC_SOFTKNEE = 0.35;

// Local contrast (CLAHE)
static const int CLAHE_TILE = 8;
static const double CLAHE_CLIPLIMIT = 2.0;

// Multi-exposure fusion
static const double EXPOSURE_GAINS[] = {0.70, 1.00, 1.35};
static const int NUM_EXPOSURES = 3;
static const double FUSION_SIGMA = 55.0;

// Diff cleanup
static const double DIFF_BLUR_SIGMA = 1.0;
static const double DIFF_CLIP_PERCENTILE = 99.7;

// ============================================================================
// Helper: percentile value from histogram
// ============================================================================
static int percentile_value(const cv::Mat& img, double pct) {
    int histSize = 256;
    float range[] = {0, 256};
    const float* histRange = {range};
    cv::Mat hist;
    cv::calcHist(&img, 1, 0, cv::Mat(), hist, 1, &histSize, &histRange);
    
    int total = img.rows * img.cols;
    int target = (int)(total * pct / 100.0);
    int cumsum = 0;
    for (int i = 0; i < 256; i++) {
        cumsum += (int)hist.at<float>(i);
        if (cumsum >= target) return i;
    }
    return 255;
}

// ============================================================================
// Helper: edge energy (sum of Sobel magnitude)
// ============================================================================
static double compute_edge_energy(const cv::Mat& img) {
    cv::Mat sx, sy, mag;
    cv::Sobel(img, sx, CV_64F, 1, 0, 3);
    cv::Sobel(img, sy, CV_64F, 0, 1, 3);
    cv::magnitude(sx, sy, mag);
    return cv::sum(mag)[0];
}

// ============================================================================
// SGHF Pipeline
// ============================================================================
SghfResult sghf_process(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    const cv::Mat& motion_mask)
{
    SghfResult result;
    result.sghf_used = false;

    if (!g_use_sghf) return result;

    // Convert to grayscale
    cv::Mat gray_curr, gray_prev;
    if (current_frame.channels() == 3)
        cv::cvtColor(current_frame, gray_curr, cv::COLOR_BGR2GRAY);
    else
        gray_curr = current_frame.clone();
    if (previous_frame.channels() == 3)
        cv::cvtColor(previous_frame, gray_prev, cv::COLOR_BGR2GRAY);
    else
        gray_prev = previous_frame.clone();

    // ---------------------------------------------------------------
    // Step 1: Build Base Diff D0
    // ---------------------------------------------------------------
    cv::Mat D0;
    cv::absdiff(gray_curr, gray_prev, D0);
    
    int ksize = (int)(DIFF_BLUR_SIGMA * 6) | 1;
    if (ksize < 3) ksize = 3;
    cv::GaussianBlur(D0, D0, cv::Size(ksize, ksize), DIFF_BLUR_SIGMA);
    
    // Clip at percentile and normalize
    int clip_val = percentile_value(D0, DIFF_CLIP_PERCENTILE);
    if (clip_val > 0 && clip_val < 255) {
        D0.convertTo(D0, CV_8U, 255.0 / clip_val, 0);
    }
    
    // Apply motion mask
    if (!motion_mask.empty()) {
        cv::bitwise_and(D0, motion_mask, D0);
    }

    result.edge_energy_before = compute_edge_energy(D0);
    cv::Scalar mean_before = cv::mean(D0);
    result.mean_intensity_before = mean_before[0];

    cv::Mat D1 = D0.clone();

    // ---------------------------------------------------------------
    // Step 2: Specular/Glare Detection + Soft Clamp
    // ---------------------------------------------------------------
    if (g_sghf_enable_specular_clamp) {
        int spec_thresh = percentile_value(D0, SPEC_CLIP_PERCENTILE);
        
        // Build specular mask
        cv::Mat S;
        cv::threshold(D0, S, spec_thresh, 255, cv::THRESH_BINARY);
        
        // Dilate to include halo
        cv::Mat dilate_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
        cv::dilate(S, S, dilate_kern, cv::Point(-1,-1), 1);
        
        // Count specular pixels
        int spec_pixels = cv::countNonZero(S);
        int total_pixels = D0.rows * D0.cols;
        result.specular_pixel_ratio = (total_pixels > 0) ? (double)spec_pixels / total_pixels : 0.0;

        // Apply soft-knee compression in specular regions
        D1.forEach<uchar>([&](uchar& pixel, const int* pos) {
            if (S.at<uchar>(pos[0], pos[1]) > 0) {
                double p = pixel;
                double norm = p / 255.0;
                double compressed = p / (1.0 + SPEC_SOFTKNEE * norm * norm);
                pixel = cv::saturate_cast<uchar>(compressed);
            }
        });
    }

    cv::Scalar mean_after_clamp = cv::mean(D1);
    result.mean_intensity_after = mean_after_clamp[0];

    // ---------------------------------------------------------------
    // Step 3: Local Contrast Enhancement (CLAHE)
    // ---------------------------------------------------------------
    cv::Mat D2 = D1;
    if (g_sghf_enable_local_contrast) {
        auto clahe = cv::createCLAHE(CLAHE_CLIPLIMIT, cv::Size(CLAHE_TILE, CLAHE_TILE));
        clahe->apply(D1, D2);
    }

    // ---------------------------------------------------------------
    // Step 4: Multi-Exposure Fusion
    // ---------------------------------------------------------------
    cv::Mat D3 = D2;
    if (g_sghf_enable_multi_exposure_fusion) {
        cv::Mat sum_weighted = cv::Mat::zeros(D2.size(), CV_64FC1);
        cv::Mat sum_weights = cv::Mat::zeros(D2.size(), CV_64FC1);
        
        cv::Mat D2_64;
        D2.convertTo(D2_64, CV_64FC1);
        
        double sigma2 = 2.0 * FUSION_SIGMA * FUSION_SIGMA;
        
        for (int k = 0; k < NUM_EXPOSURES; k++) {
            cv::Mat Ek;
            D2_64.convertTo(Ek, CV_64FC1, EXPOSURE_GAINS[k], 0);
            // Clamp to 0..255
            cv::min(Ek, 255.0, Ek);
            cv::max(Ek, 0.0, Ek);
            
            // Well-exposedness weight: favor mid-tones (~128)
            cv::Mat diff_from_mid;
            cv::subtract(Ek, cv::Scalar(128.0), diff_from_mid);
            cv::Mat wk;
            cv::multiply(diff_from_mid, diff_from_mid, wk, -1.0 / sigma2);
            cv::exp(wk, wk);
            
            cv::Mat weighted;
            cv::multiply(wk, Ek, weighted);
            sum_weighted += weighted;
            sum_weights += wk;
        }
        
        // Avoid division by zero
        sum_weights = cv::max(sum_weights, EPS);
        
        cv::Mat fused;
        cv::divide(sum_weighted, sum_weights, fused);
        
        // Normalize to 0..255
        double minv, maxv;
        cv::minMaxLoc(fused, &minv, &maxv);
        if (maxv - minv > EPS) {
            fused = (fused - minv) * (255.0 / (maxv - minv));
        }
        fused.convertTo(D3, CV_8UC1);
    }

    result.edge_energy_after = compute_edge_energy(D3);

    // ---------------------------------------------------------------
    // Step 5: Fallback Guard
    // ---------------------------------------------------------------
    // Threshold + morph for blob analysis
    cv::Mat binary;
    cv::threshold(D3, binary, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    
    cv::Mat open_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::Mat close_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(5, 5));
    cv::morphologyEx(binary, binary, cv::MORPH_OPEN, open_kern, cv::Point(-1,-1), 1);
    cv::morphologyEx(binary, binary, cv::MORPH_CLOSE, close_kern, cv::Point(-1,-1), 1);
    
    cv::Mat labels, stats, centroids;
    result.blob_count = cv::connectedComponentsWithStats(binary, labels, stats, centroids) - 1;
    result.dart_area = cv::countNonZero(binary);

    bool use_sghf = true;
    if (g_sghf_fallback_to_legacy_diff) {
        // Fallback if edge energy dropped significantly or dart area too small
        if (result.edge_energy_after < result.edge_energy_before * 0.3) {
            use_sghf = false;
        }
        if (result.dart_area < 30) {
            use_sghf = false;
        }
    }

    // ---------------------------------------------------------------
    // Step 6: Output
    // ---------------------------------------------------------------
    if (use_sghf) {
        result.processed_diff = D3;
        result.sghf_used = true;
    } else {
        result.processed_diff = D0;
        result.sghf_used = false;
    }

    return result;
}

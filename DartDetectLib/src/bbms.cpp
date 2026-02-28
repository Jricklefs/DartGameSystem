/**
 * bbms.cpp - Phase 21: Board Background Model Subtraction
 *
 * Produces a clean "dart-only" differential image (D_bbms) by:
 *   Step 1: Background model (or fallback to raw frame)
 *   Step 2: Illumination normalization
 *   Step 3: Background subtraction
 *   Step 4: Shadow suppression
 *   Step 5: Binary mask cleanup
 *
 * D_bbms replaces the legacy absdiff input to IQDL.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <numeric>
#include <cmath>
#include <iostream>
#include <mutex>
#include <deque>

// ============================================================================
// BBMS Feature Flags
// ============================================================================
static bool g_use_bbms = false;
static bool g_bbms_enable_running_bg = true;
static bool g_bbms_enable_per_pixel_median = true;
static bool g_bbms_enable_illumination_normalize = true;
static bool g_bbms_enable_shadow_suppress = true;
static bool g_bbms_fallback_to_legacy_diff = true;

int set_bbms_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseBBMS") { g_use_bbms = (value != 0); return 0; }
    if (s == "BBMS_EnableRunningBackground") { g_bbms_enable_running_bg = (value != 0); return 0; }
    if (s == "BBMS_EnablePerPixelMedian") { g_bbms_enable_per_pixel_median = (value != 0); return 0; }
    if (s == "BBMS_EnableIlluminationNormalize") { g_bbms_enable_illumination_normalize = (value != 0); return 0; }
    if (s == "BBMS_EnableShadowSuppress") { g_bbms_enable_shadow_suppress = (value != 0); return 0; }
    if (s == "BBMS_FallbackToLegacyDiff") { g_bbms_fallback_to_legacy_diff = (value != 0); return 0; }
    return -1;
}

bool bbms_is_enabled() { return g_use_bbms; }

// ============================================================================
// BBMS Parameters
// ============================================================================
static const double BBMS_EPS = 1e-6;
static const int    BBMS_BG_FRAME_COUNT = 30;
static const double BBMS_NORM_BLUR_SIGMA = 12.0;
static const double BBMS_NORM_CLAMP_MIN = 0.6;
static const double BBMS_NORM_CLAMP_MAX = 1.6;
static const double BBMS_DIFF_BLUR_SIGMA = 1.2;
static const double BBMS_DIFF_CLIP_PERCENTILE = 99.7;
static const double BBMS_SHADOW_LOW_FREQ_SIGMA = 20.0;
static const double BBMS_SHADOW_SUPPRESS_WEIGHT = 0.5;

// ============================================================================
// Background Model Storage (per camera)
// ============================================================================
struct BgModel {
    std::deque<cv::Mat> frames;
    cv::Mat median_bg;
    bool ready = false;
    std::mutex mtx;
};

static std::map<std::string, BgModel> g_bg_models;
static std::mutex g_bg_map_mtx;

// ============================================================================
// Main BBMS function
// ============================================================================
BbmsResult run_bbms(
    const std::string& cam_id,
    const cv::Mat& current_frame,
    const cv::Mat& background_frame,
    const cv::Mat& motion_mask)
{
    BbmsResult res;
    res.bbms_used = false;
    res.bbms_bg_ready = false;
    res.fallback_to_legacy_diff = true;
    res.bg_buffer_count = 0;
    res.illumination_ratio_mean = 1.0;
    res.illumination_ratio_min = 1.0;
    res.illumination_ratio_max = 1.0;
    res.blob_count = 0;
    res.dart_area = 0;
    res.edge_energy = 0.0;

    if (!g_use_bbms) return res;

    cv::Mat gray_curr, gray_bg;
    if (current_frame.channels() == 3)
        cv::cvtColor(current_frame, gray_curr, cv::COLOR_BGR2GRAY);
    else
        gray_curr = current_frame.clone();

    if (background_frame.channels() == 3)
        cv::cvtColor(background_frame, gray_bg, cv::COLOR_BGR2GRAY);
    else
        gray_bg = background_frame.clone();

    if (gray_curr.empty() || gray_bg.empty()) return res;
    if (gray_curr.size() != gray_bg.size()) return res;

    // Step 1: Background Model
    cv::Mat B;
    {
        std::lock_guard<std::mutex> map_lock(g_bg_map_mtx);
        auto& model = g_bg_models[cam_id];
        std::lock_guard<std::mutex> lock(model.mtx);

        if (g_bbms_enable_running_bg && g_bbms_enable_per_pixel_median
            && !model.frames.empty() && model.ready) {
            B = model.median_bg.clone();
            res.bg_buffer_count = (int)model.frames.size();
            res.bbms_bg_ready = ((int)model.frames.size() >= BBMS_BG_FRAME_COUNT);
        } else {
            B = gray_bg.clone();
            res.bg_buffer_count = 1;
            res.bbms_bg_ready = true;
        }
    }

    cv::Mat F = gray_curr.clone();

    // Step 2: Illumination Normalization
    if (g_bbms_enable_illumination_normalize) {
        int ksize = (int)(BBMS_NORM_BLUR_SIGMA * 6) | 1;
        if (ksize < 3) ksize = 3;

        cv::Mat F_f, B_f, L_F, L_B;
        F.convertTo(F_f, CV_64F);
        B.convertTo(B_f, CV_64F);

        cv::GaussianBlur(F_f, L_F, cv::Size(ksize, ksize), BBMS_NORM_BLUR_SIGMA);
        cv::GaussianBlur(B_f, L_B, cv::Size(ksize, ksize), BBMS_NORM_BLUR_SIGMA);

        cv::Mat R = cv::Mat::zeros(F.size(), CV_64F);
        for (int r = 0; r < R.rows; r++) {
            double* r_row = R.ptr<double>(r);
            const double* lf_row = L_F.ptr<double>(r);
            const double* lb_row = L_B.ptr<double>(r);
            for (int c = 0; c < R.cols; c++) {
                double denom = std::max(BBMS_EPS, lb_row[c]);
                double ratio = lf_row[c] / denom;
                r_row[c] = std::max(BBMS_NORM_CLAMP_MIN,
                           std::min(BBMS_NORM_CLAMP_MAX, ratio));
            }
        }

        cv::Scalar r_mean_s, r_std_s;
        cv::meanStdDev(R, r_mean_s, r_std_s);
        double r_min_val, r_max_val;
        cv::minMaxLoc(R, &r_min_val, &r_max_val);
        res.illumination_ratio_mean = r_mean_s[0];
        res.illumination_ratio_min = r_min_val;
        res.illumination_ratio_max = r_max_val;

        cv::Mat F_norm = cv::Mat::zeros(F.size(), CV_64F);
        for (int r = 0; r < F_norm.rows; r++) {
            double* fn_row = F_norm.ptr<double>(r);
            const double* f_row = F_f.ptr<double>(r);
            const double* r_row = R.ptr<double>(r);
            for (int c = 0; c < F_norm.cols; c++) {
                fn_row[c] = f_row[c] / std::max(BBMS_EPS, r_row[c]);
            }
        }

        F_norm.convertTo(F, CV_8U);
    }

    // Step 3: Background Subtraction
    cv::Mat D0;
    cv::absdiff(F, B, D0);

    {
        int ksize = (int)(BBMS_DIFF_BLUR_SIGMA * 6) | 1;
        if (ksize < 3) ksize = 3;
        cv::GaussianBlur(D0, D0, cv::Size(ksize, ksize), BBMS_DIFF_BLUR_SIGMA);
    }

    // Clip at percentile
    {
        int histSize = 256;
        float range[] = {0, 256};
        const float* histRange = {range};
        cv::Mat hist;
        cv::calcHist(&D0, 1, 0, cv::Mat(), hist, 1, &histSize, &histRange);
        int total = D0.rows * D0.cols;
        int target = (int)(total * BBMS_DIFF_CLIP_PERCENTILE / 100.0);
        int cumsum = 0;
        int clip_val = 255;
        for (int i = 0; i < 256; i++) {
            cumsum += (int)hist.at<float>(i);
            if (cumsum >= target) { clip_val = i; break; }
        }
        if (clip_val > 0 && clip_val < 255) {
            D0.convertTo(D0, CV_8U, 255.0 / clip_val, 0);
        }
    }

    // Apply motion mask
    cv::Mat D2;
    if (!motion_mask.empty() && motion_mask.size() == D0.size()) {
        cv::bitwise_and(D0, motion_mask, D2);
    } else {
        D2 = D0;
    }

    // Step 4: Shadow Suppression
    cv::Mat D3;
    if (g_bbms_enable_shadow_suppress) {
        int ksize = (int)(BBMS_SHADOW_LOW_FREQ_SIGMA * 6) | 1;
        if (ksize < 3) ksize = 3;
        cv::Mat D_low;
        cv::GaussianBlur(D2, D_low, cv::Size(ksize, ksize), BBMS_SHADOW_LOW_FREQ_SIGMA);

        D3 = cv::Mat::zeros(D2.size(), CV_8U);
        for (int r = 0; r < D2.rows; r++) {
            const uchar* d2_row = D2.ptr<uchar>(r);
            const uchar* dl_row = D_low.ptr<uchar>(r);
            uchar* d3_row = D3.ptr<uchar>(r);
            for (int c = 0; c < D2.cols; c++) {
                double val = (double)d2_row[c] - BBMS_SHADOW_SUPPRESS_WEIGHT * dl_row[c];
                d3_row[c] = (uchar)std::max(0.0, val);
            }
        }
    } else {
        D3 = D2;
    }

    // Step 5: Binary Mask for Debug
    cv::Mat T;
    cv::threshold(D3, T, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);

    cv::Mat open_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
    cv::Mat close_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(5, 5));
    cv::morphologyEx(T, T, cv::MORPH_OPEN, open_kern, cv::Point(-1,-1), 1);
    cv::morphologyEx(T, T, cv::MORPH_CLOSE, close_kern, cv::Point(-1,-1), 1);

    cv::Mat labels;
    res.blob_count = cv::connectedComponents(T, labels) - 1;
    res.dart_area = cv::countNonZero(T);

    cv::Mat sx, sy, smag;
    cv::Sobel(D3, sx, CV_64F, 1, 0, 3);
    cv::Sobel(D3, sy, CV_64F, 0, 1, 3);
    cv::magnitude(sx, sy, smag);
    res.edge_energy = cv::sum(smag)[0];

    // Fallback check
    double d3_mean = cv::mean(D3)[0];
    bool extremely_weak = (d3_mean < 1.0 && res.dart_area < 20);

    if (g_bbms_fallback_to_legacy_diff && extremely_weak) {
        res.bbms_used = false;
        res.fallback_to_legacy_diff = true;
        return res;
    }

    res.D_bbms = D3.clone();
    res.mask_bbms = T.clone();
    res.bbms_used = true;
    res.fallback_to_legacy_diff = false;

    return res;
}

void bbms_update_background(const std::string& cam_id, const cv::Mat& empty_board_frame) {
    if (!g_use_bbms || !g_bbms_enable_running_bg) return;

    cv::Mat gray;
    if (empty_board_frame.channels() == 3)
        cv::cvtColor(empty_board_frame, gray, cv::COLOR_BGR2GRAY);
    else
        gray = empty_board_frame.clone();

    std::lock_guard<std::mutex> map_lock(g_bg_map_mtx);
    auto& model = g_bg_models[cam_id];
    std::lock_guard<std::mutex> lock(model.mtx);

    model.frames.push_back(gray);
    if ((int)model.frames.size() > BBMS_BG_FRAME_COUNT)
        model.frames.pop_front();

    if (g_bbms_enable_per_pixel_median && (int)model.frames.size() >= 3) {
        int n = (int)model.frames.size();
        int rows = gray.rows, cols = gray.cols;
        model.median_bg = cv::Mat::zeros(rows, cols, CV_8U);
        std::vector<uchar> vals(n);
        for (int r = 0; r < rows; r++) {
            for (int c = 0; c < cols; c++) {
                for (int f = 0; f < n; f++) {
                    vals[f] = model.frames[f].at<uchar>(r, c);
                }
                std::nth_element(vals.begin(), vals.begin() + n/2, vals.begin() + n);
                model.median_bg.at<uchar>(r, c) = vals[n/2];
            }
        }
        model.ready = true;
    }
}

void bbms_clear_model(const std::string& cam_id) {
    std::lock_guard<std::mutex> map_lock(g_bg_map_mtx);
    auto it = g_bg_models.find(cam_id);
    if (it != g_bg_models.end()) {
        std::lock_guard<std::mutex> lock(it->second.mtx);
        it->second.frames.clear();
        it->second.median_bg = cv::Mat();
        it->second.ready = false;
    }
}

void bbms_clear_all_models() {
    std::lock_guard<std::mutex> map_lock(g_bg_map_mtx);
    g_bg_models.clear();
}

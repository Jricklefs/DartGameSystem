/**
 * dea.cpp - Phase 22: Directional Edge Amplification
 *
 * Amplifies thin elongated edge structures (barrel edges) in the diff image
 * before IQDL processes it. Uses Sobel gradients, directional weighting
 * aligned to a PCA pre-pass axis estimate, and structure tensor linearity boosting.
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <numeric>
#include <cmath>
#include <iostream>

// ============================================================================
// Feature flags (default OFF for master, sub-flags default ON)
// ============================================================================
static bool g_use_dea = false;
static bool g_dea_gradient_boost = true;
static bool g_dea_directional_weighting = true;
static bool g_dea_structure_enhance = true;
static bool g_dea_fallback_to_legacy = true;

int set_dea_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseDEA") { g_use_dea = (value != 0); return 0; }
    if (s == "DEA_EnableGradientBoost") { g_dea_gradient_boost = (value != 0); return 0; }
    if (s == "DEA_EnableDirectionalWeighting") { g_dea_directional_weighting = (value != 0); return 0; }
    if (s == "DEA_EnableStructureEnhance") { g_dea_structure_enhance = (value != 0); return 0; }
    if (s == "DEA_FallbackToLegacyDiff") { g_dea_fallback_to_legacy = (value != 0); return 0; }
    return -1;
}

bool dea_is_enabled() { return g_use_dea; }

// ============================================================================
// Parameters
// ============================================================================
static const double EPS = 1e-6;
static const int    SOBEL_KERNEL = 3;
static const double GRADIENT_BLUR_SIGMA = 0.8;
static const double DIRECTIONAL_WEIGHT_POWER = 2.0;
static const double MAX_DIRECTIONAL_GAIN = 2.5;
static const double STRUCTURE_TENSOR_BLUR = 2.0;
static const double LINEARITY_THRESHOLD = 0.6;
static const double LINEARITY_GAIN = 1.8;
static const double BOOST_CLIP_PERCENTILE = 99.5;
static const bool   FINAL_NORMALIZE = true;

// Fallback thresholds
static const double ENERGY_MIN_THRESHOLD = 50.0;
static const int    BLOB_COUNT_MAX = 20;

// ============================================================================
// Helper: percentile clip value
// ============================================================================
static double percentile_value(const cv::Mat& img, double pct) {
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
        if (cumsum >= target) return (double)i;
    }
    return 255.0;
}

// ============================================================================
// Step 3: PCA pre-pass axis estimate from thresholded gradient
// ============================================================================
static bool estimate_axis_pca(const cv::Mat& G, cv::Point2d& axis_out) {
    // Threshold at 70th percentile
    double thresh_val = percentile_value(G, 70.0);
    if (thresh_val < 5.0) thresh_val = 5.0;

    cv::Mat binary;
    cv::threshold(G, binary, thresh_val, 255, cv::THRESH_BINARY);

    // Collect non-zero points
    std::vector<cv::Point> pts;
    cv::findNonZero(binary, pts);
    if ((int)pts.size() < 20) return false;

    // PCA
    cv::Mat data((int)pts.size(), 2, CV_64F);
    for (int i = 0; i < (int)pts.size(); i++) {
        data.at<double>(i, 0) = pts[i].x;
        data.at<double>(i, 1) = pts[i].y;
    }
    cv::PCA pca(data, cv::Mat(), cv::PCA::DATA_AS_ROW);

    cv::Mat eigvec = pca.eigenvectors;
    axis_out.x = eigvec.at<double>(0, 0);
    axis_out.y = eigvec.at<double>(0, 1);

    double len = std::sqrt(axis_out.x * axis_out.x + axis_out.y * axis_out.y);
    if (len < EPS) return false;
    axis_out.x /= len;
    axis_out.y /= len;
    return true;
}

// ============================================================================
// Main DEA pipeline
// ============================================================================
DeaResult run_dea(const cv::Mat& D_legacy, const cv::Mat& motion_mask) {
    DeaResult res;
    res.dea_used = false;
    res.axis_pre_valid = false;
    res.mean_alignment = 0.0;
    res.linearity_mean = 0.0;
    res.energy_before = 0.0;
    res.energy_after = 0.0;

    if (!g_use_dea || D_legacy.empty()) {
        res.D_dea = D_legacy.clone();
        return res;
    }

    // Ensure grayscale
    cv::Mat D0;
    if (D_legacy.channels() == 3) {
        cv::cvtColor(D_legacy, D0, cv::COLOR_BGR2GRAY);
    } else {
        D0 = D_legacy.clone();
    }

    res.energy_before = cv::sum(D0)[0];

    // Step 1: Gaussian blur
    int ksize = (int)(GRADIENT_BLUR_SIGMA * 6) | 1;
    if (ksize < 3) ksize = 3;
    cv::GaussianBlur(D0, D0, cv::Size(ksize, ksize), GRADIENT_BLUR_SIGMA);

    // Step 2: Sobel gradients
    cv::Mat Gx, Gy;
    cv::Sobel(D0, Gx, CV_64F, 1, 0, SOBEL_KERNEL);
    cv::Sobel(D0, Gy, CV_64F, 0, 1, SOBEL_KERNEL);

    cv::Mat G;
    cv::magnitude(Gx, Gy, G);

    // Convert G to 8U for percentile helpers
    cv::Mat G8;
    {
        double gmin, gmax;
        cv::minMaxLoc(G, &gmin, &gmax);
        if (gmax - gmin > EPS) {
            G.convertTo(G8, CV_8U, 255.0 / (gmax - gmin), -gmin * 255.0 / (gmax - gmin));
        } else {
            G8 = cv::Mat::zeros(G.size(), CV_8U);
        }
    }

    if (!g_dea_gradient_boost) {
        res.D_dea = D_legacy.clone();
        return res;
    }

    // Step 3: PCA pre-axis estimate
    cv::Point2d axis_pre(0, 0);
    bool axis_valid = estimate_axis_pca(G8, axis_pre);
    res.axis_pre_valid = axis_valid;

    // Step 4: Directional weighting
    cv::Mat w_dir = cv::Mat::ones(G.size(), CV_64F);
    if (g_dea_directional_weighting && axis_valid) {
        double sum_alignment = 0.0;
        int count = 0;
        for (int y = 0; y < G.rows; y++) {
            const double* gx_row = Gx.ptr<double>(y);
            const double* gy_row = Gy.ptr<double>(y);
            double* wd_row = w_dir.ptr<double>(y);
            for (int x = 0; x < G.cols; x++) {
                double gxv = gx_row[x];
                double gyv = gy_row[x];
                double glen = std::sqrt(gxv * gxv + gyv * gyv);
                if (glen < EPS) continue;

                double dx = gxv / glen;
                double dy = gyv / glen;
                double alignment = std::abs(dx * axis_pre.x + dy * axis_pre.y);

                double w = 1.0 + std::pow(alignment, DIRECTIONAL_WEIGHT_POWER);
                w = std::min(w, MAX_DIRECTIONAL_GAIN);
                wd_row[x] = w;

                sum_alignment += alignment;
                count++;
            }
        }
        if (count > 0) res.mean_alignment = sum_alignment / count;
    }

    // Step 5: Structure tensor linearity boost
    cv::Mat w_lin = cv::Mat::ones(G.size(), CV_64F);
    if (g_dea_structure_enhance) {
        cv::Mat Gx2, Gy2, Gxy;
        cv::multiply(Gx, Gx, Gx2);
        cv::multiply(Gy, Gy, Gy2);
        cv::multiply(Gx, Gy, Gxy);

        int st_ksize = (int)(STRUCTURE_TENSOR_BLUR * 6) | 1;
        if (st_ksize < 3) st_ksize = 3;
        cv::GaussianBlur(Gx2, Gx2, cv::Size(st_ksize, st_ksize), STRUCTURE_TENSOR_BLUR);
        cv::GaussianBlur(Gy2, Gy2, cv::Size(st_ksize, st_ksize), STRUCTURE_TENSOR_BLUR);
        cv::GaussianBlur(Gxy, Gxy, cv::Size(st_ksize, st_ksize), STRUCTURE_TENSOR_BLUR);

        double sum_lin = 0.0;
        int lin_count = 0;

        for (int y = 0; y < G.rows; y++) {
            const double* jxx = Gx2.ptr<double>(y);
            const double* jyy = Gy2.ptr<double>(y);
            const double* jxy = Gxy.ptr<double>(y);
            double* wl = w_lin.ptr<double>(y);
            for (int x = 0; x < G.cols; x++) {
                double a = jxx[x], b = jxy[x], d = jyy[x];
                double trace = a + d;
                double det = a * d - b * b;
                double disc = trace * trace - 4.0 * det;
                if (disc < 0) disc = 0;
                double sq = std::sqrt(disc);
                double lambda1 = (trace + sq) / 2.0;
                double lambda2 = (trace - sq) / 2.0;

                double linearity = (lambda1 - lambda2) / std::max(EPS, lambda1);
                sum_lin += linearity;
                lin_count++;

                if (linearity > LINEARITY_THRESHOLD) {
                    wl[x] = LINEARITY_GAIN;
                }
            }
        }
        if (lin_count > 0) res.linearity_mean = sum_lin / lin_count;
    }

    // Step 6: Combine amplification  D1 = D0 + (G * w_dir * w_lin)
    cv::Mat D0_64;
    D0.convertTo(D0_64, CV_64F);

    cv::Mat amplified = G.mul(w_dir).mul(w_lin);
    cv::Mat D1 = D0_64 + amplified;

    // Clip at percentile
    {
        double dmin, dmax;
        cv::minMaxLoc(D1, &dmin, &dmax);
        cv::Mat D1_8;
        if (dmax - dmin > EPS) {
            D1.convertTo(D1_8, CV_8U, 255.0 / (dmax - dmin), -dmin * 255.0 / (dmax - dmin));
        } else {
            D1_8 = cv::Mat::zeros(D1.size(), CV_8U);
        }
        double clip_val = percentile_value(D1_8, BOOST_CLIP_PERCENTILE);
        double clip_in_d1 = dmin + clip_val * (dmax - dmin) / 255.0;
        cv::min(D1, clip_in_d1, D1);
    }

    // Normalize to 0-255
    cv::Mat D2;
    if (FINAL_NORMALIZE) {
        double dmin, dmax;
        cv::minMaxLoc(D1, &dmin, &dmax);
        if (dmax - dmin > EPS) {
            D1.convertTo(D2, CV_8U, 255.0 / (dmax - dmin), -dmin * 255.0 / (dmax - dmin));
        } else {
            D2 = cv::Mat::zeros(D1.size(), CV_8U);
        }
    } else {
        D1.convertTo(D2, CV_8U);
    }

    // Apply motion mask if provided
    if (!motion_mask.empty() && motion_mask.size() == D2.size()) {
        cv::bitwise_and(D2, motion_mask, D2);
    }

    res.energy_after = cv::sum(D2)[0];

    // Step 7: Fallback safety
    if (g_dea_fallback_to_legacy) {
        double total_energy = res.energy_after;
        bool low_energy = (total_energy < ENERGY_MIN_THRESHOLD);

        bool high_noise = false;
        {
            cv::Mat binary;
            cv::threshold(D2, binary, 20, 255, cv::THRESH_BINARY);
            cv::Mat labels, stats, centroids;
            int n = cv::connectedComponentsWithStats(binary, labels, stats, centroids);
            if (n - 1 > BLOB_COUNT_MAX) high_noise = true;
        }

        if (low_energy || high_noise) {
            res.D_dea = D_legacy.clone();
            res.dea_used = false;
            return res;
        }
    }

    res.D_dea = D2;
    res.dea_used = true;
    return res;
}

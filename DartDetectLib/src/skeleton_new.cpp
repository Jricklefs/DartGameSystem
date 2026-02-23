/**
 * skeleton.cpp - Skeleton/Hough detection, barrel-centric detection, PCA blob chain tip
 * 
 * Ported from Python: skeleton_detection.py detect_dart()
 */
// === ACCURACY IMPROVEMENT NOTES (Feb 19, 2026) ===
// Tried and kept:
//   - Morph kernels 7->3: +26% on edge-case game (mask.cpp)
//   - Dual-axis barrel splitting: handles angled darts (skeleton.cpp)
//   - Mid-ring TPS points: +1 dart accuracy (triangulation.cpp)
//   - TPS precompute at init: 300ms->178ms per dart (dart_detect.cpp)
//   - Hough averaging top-3: neutral but reduces noise (skeleton.cpp)
//   - Reduced ref_angle bias: neutral (skeleton.cpp)
//
// Tried and reverted:
//   - RANSAC replacing Hough: 89%->65%, RANSAC worse on thin barrel pixels
//   - Multiplier voting across cameras: 92%->79%, per-camera scoring less accurate than intersection
//   - Wire tolerance +1.4mm: 90%->86%, overcorrected (too many singles->triples)
//   - 40-angle TPS (midpoint angles): caused regression + O(n^3) perf hit
//
// Current best: 91/101 (90%) across 3 benchmark games, ~178ms avg
// Remaining errors: genuine wire-boundary darts (ring boundary + adjacent segment)
//
// Phase 3 changes (Feb 20, 2026):
//   - RANSAC angle tolerance: 75 -> 60 degrees (Change 2)
//   - Zhang-Suen thinning: bounding-rect optimization + ptr access (Change 5)
//   - Width profiling: pre-bucket dart pixels by row/col (Change 6)
//   - Resolution-adaptive thresholds via scale factor (Change 4)

#include "dart_detect_internal.h"
// Zhang-Suen thinning (replaces cv::ximgproc::thinning to avoid contrib dependency)
// Phase 3: Optimized — compute bounding rect of non-zero pixels, iterate only within it,
// use ptr<uchar> row access instead of .at<uchar>(row, col) for inner loop.
namespace {
void zhangSuenThinning(const cv::Mat& src, cv::Mat& dst) {
    src.copyTo(dst);
    dst /= 255;  // Work with 0/1 values
    
    // Phase 3: Compute bounding rect of non-zero pixels to limit iteration
    cv::Rect bounds;
    {
        std::vector<cv::Point> nz;
        cv::findNonZero(dst, nz);
        if (nz.empty()) { dst *= 255; return; }
        bounds = cv::boundingRect(nz);
        // Expand by 1 pixel for neighbor access, clamp to image
        int r0 = std::max(1, bounds.y - 1);
        int r1 = std::min(dst.rows - 2, bounds.y + bounds.height + 1);
        int c0 = std::max(1, bounds.x - 1);
        int c1 = std::min(dst.cols - 2, bounds.x + bounds.width + 1);
        bounds = cv::Rect(c0, r0, c1 - c0, r1 - r0);
    }
    
    cv::Mat prev = cv::Mat::zeros(dst.size(), CV_8UC1);
    cv::Mat marker;
    
    while (true) {
        dst.copyTo(prev);
        
        // Sub-iteration 1
        marker = cv::Mat::zeros(dst.size(), CV_8UC1);
        for (int i = bounds.y; i < bounds.y + bounds.height; i++) {
            const uchar* row_prev = dst.ptr<uchar>(i - 1);
            const uchar* row_curr = dst.ptr<uchar>(i);
            const uchar* row_next = dst.ptr<uchar>(i + 1);
            uchar* mark_row = marker.ptr<uchar>(i);
            for (int j = bounds.x; j < bounds.x + bounds.width; j++) {
                if (row_curr[j] != 1) continue;
                
                uchar p2 = row_prev[j];
                uchar p3 = row_prev[j+1];
                uchar p4 = row_curr[j+1];
                uchar p5 = row_next[j+1];
                uchar p6 = row_next[j];
                uchar p7 = row_next[j-1];
                uchar p8 = row_curr[j-1];
                uchar p9 = row_prev[j-1];
                
                int B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                if (B < 2 || B > 6) continue;
                
                int A = (p2==0 && p3==1) + (p3==0 && p4==1) + (p4==0 && p5==1) +
                         (p5==0 && p6==1) + (p6==0 && p7==1) + (p7==0 && p8==1) +
                         (p8==0 && p9==1) + (p9==0 && p2==1);
                if (A != 1) continue;
                
                if (p2 * p4 * p6 != 0) continue;
                if (p4 * p6 * p8 != 0) continue;
                
                mark_row[j] = 1;
            }
        }
        dst -= marker;
        
        // Sub-iteration 2
        marker = cv::Mat::zeros(dst.size(), CV_8UC1);
        for (int i = bounds.y; i < bounds.y + bounds.height; i++) {
            const uchar* row_prev = dst.ptr<uchar>(i - 1);
            const uchar* row_curr = dst.ptr<uchar>(i);
            const uchar* row_next = dst.ptr<uchar>(i + 1);
            uchar* mark_row = marker.ptr<uchar>(i);
            for (int j = bounds.x; j < bounds.x + bounds.width; j++) {
                if (row_curr[j] != 1) continue;
                
                uchar p2 = row_prev[j];
                uchar p3 = row_prev[j+1];
                uchar p4 = row_curr[j+1];
                uchar p5 = row_next[j+1];
                uchar p6 = row_next[j];
                uchar p7 = row_next[j-1];
                uchar p8 = row_curr[j-1];
                uchar p9 = row_prev[j-1];
                
                int B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                if (B < 2 || B > 6) continue;
                
                int A = (p2==0 && p3==1) + (p3==0 && p4==1) + (p4==0 && p5==1) +
                         (p5==0 && p6==1) + (p6==0 && p7==1) + (p7==0 && p8==1) +
                         (p8==0 && p9==1) + (p9==0 && p2==1);
                if (A != 1) continue;
                
                if (p2 * p4 * p8 != 0) continue;
                if (p2 * p6 * p8 != 0) continue;
                
                mark_row[j] = 1;
            }
        }
        dst -= marker;
        
        // Check convergence
        if (cv::countNonZero(dst - prev) == 0) break;
    }
    
    dst *= 255;  // Back to 0/255
}
} // anonymous namespace
#include <algorithm>
#include <set>
#include <numeric>
#include <random>
#include <queue>
#include <map>

// ============================================================================
// Helper: Find flight blob (largest contour)
// ============================================================================
struct FlightBlob {
    Point2f centroid;
    std::vector<cv::Point> contour;
    cv::Rect bbox;
};

static std::optional<FlightBlob> find_flight_blob(const cv::Mat& mask, int min_area = 80)
{
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(mask, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
    if (contours.empty()) return std::nullopt;
    
    auto it = std::max_element(contours.begin(), contours.end(),
        [](const auto& a, const auto& b) { return cv::contourArea(a) < cv::contourArea(b); });
    
    if (cv::contourArea(*it) < min_area) return std::nullopt;
    
    cv::Moments M = cv::moments(*it);
    if (M.m00 == 0) return std::nullopt;
    
    FlightBlob fb;
    fb.centroid = Point2f(M.m10 / M.m00, M.m01 / M.m00);
    fb.contour = *it;
    fb.bbox = cv::boundingRect(*it);
    return fb;
}

// ============================================================================
// Helper: Sub-pixel tip refinement
// ============================================================================
static double bilinear_sample(const cv::Mat& gray, double px, double py) {
    int x0 = (int)std::floor(px), y0 = (int)std::floor(py);
    int x1 = x0 + 1, y1 = y0 + 1;
    if (x0 < 0 || y0 < 0 || x1 >= gray.cols || y1 >= gray.rows) return 0.0;
    double fx = px - x0, fy = py - y0;
    double v00 = gray.at<uchar>(y0, x0);
    double v10 = gray.at<uchar>(y0, x1);
    double v01 = gray.at<uchar>(y1, x0);
    double v11 = gray.at<uchar>(y1, x1);
    return v00*(1-fx)*(1-fy) + v10*fx*(1-fy) + v01*(1-fx)*fy + v11*fx*fy;
}

// Direction-constrained tip refinement (Phase 2, Feb 20 2026)
static Point2f refine_tip_subpixel(Point2f tip, const cv::Mat& gray, const cv::Mat& mask, int roi_size = 10,
                                    double barrel_vx = 0.0, double barrel_vy = 0.0)
{
    (void)mask; (void)roi_size;
    int h = gray.rows, w = gray.cols;
    int walk_px = 20;
    double best_grad = 0.0;
    Point2f best_pt = tip;

    double blen = std::sqrt(barrel_vx*barrel_vx + barrel_vy*barrel_vy);
    if (blen > 0.1) {
        double dvx = barrel_vx / blen;
        double dvy = barrel_vy / blen;
        for (int step = -walk_px; step <= walk_px; ++step) {
            double px = tip.x + dvx * step;
            double py = tip.y + dvy * step;
            if (px < 2 || py < 2 || px >= w-2 || py >= h-2) continue;
            double gx = bilinear_sample(gray, px+1, py) - bilinear_sample(gray, px-1, py);
            double gy = bilinear_sample(gray, px, py+1) - bilinear_sample(gray, px, py-1);
            double grad = gx*gx + gy*gy;
            if (grad > best_grad) { best_grad = grad; best_pt = Point2f(px, py); }
        }
    } else {
        for (int step = -walk_px; step <= walk_px; ++step) {
            for (int perp = -2; perp <= 2; ++perp) {
                double px_h = tip.x + step, py_h = tip.y + perp;
                if (px_h >= 2 && py_h >= 2 && px_h < w-2 && py_h < h-2) {
                    double gx = bilinear_sample(gray, px_h+1, py_h) - bilinear_sample(gray, px_h-1, py_h);
                    double gy = bilinear_sample(gray, px_h, py_h+1) - bilinear_sample(gray, px_h, py_h-1);
                    double grad = gx*gx + gy*gy;
                    if (grad > best_grad) { best_grad = grad; best_pt = Point2f(px_h, py_h); }
                }
                double px_v = tip.x + perp, py_v = tip.y + step;
                if (px_v >= 2 && py_v >= 2 && px_v < w-2 && py_v < h-2) {
                    double gx = bilinear_sample(gray, px_v+1, py_v) - bilinear_sample(gray, px_v-1, py_v);
                    double gy = bilinear_sample(gray, px_v, py_v+1) - bilinear_sample(gray, px_v, py_v-1);
                    double grad = gx*gx + gy*gy;
                    if (grad > best_grad) { best_grad = grad; best_pt = Point2f(px_v, py_v); }
                }
            }
        }
    }
    return best_pt;
}

// ============================================================================
// Main detection function
// ============================================================================

DetectionResult detect_dart(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    Point2f board_center,
    const std::vector<cv::Mat>& prev_dart_masks,
    int diff_threshold,
    double resolution_scale)
{
    DetectionResult result;
    
    // Phase 3: Resolution-scaled constants
    double rs = (resolution_scale > 0.01) ? resolution_scale : 1.0;
    int blob_chain_dist = scale_px(BLOB_CHAIN_DIST_BASE, rs);
    int morph_close_k = scale_px_odd(MORPH_CLOSE_KERNEL_SIZE_BASE, rs);
    double mask_quality_thr = scale_d(MASK_QUALITY_THRESHOLD_BASE, rs * rs); // area scales quadratically
    double barrel_width_max = scale_d(BARREL_WIDTH_MAX_BASE, rs);
    double dart_length_min = scale_d(DART_LENGTH_MIN_BASE, rs);
    double ransac_threshold = scale_d(RANSAC_THRESHOLD_BASE, rs);
    double ransac_min_pair = scale_d(RANSAC_MIN_PAIR_DIST_BASE, rs);
    
    // Step 1: Motion mask
    auto mmr = compute_motion_mask(current_frame, previous_frame, 5, diff_threshold);
    cv::Mat motion_mask = mmr.mask;
    cv::Mat positive_mask = mmr.positive_mask;
    
    // Step 2: Pixel segmentation for dart 2+
    if (!prev_dart_masks.empty()) {
        auto seg = compute_pixel_segmentation(
            current_frame, previous_frame, prev_dart_masks, diff_threshold, 5, &mmr);
        motion_mask = seg.new_mask;
        cv::bitwise_and(positive_mask, motion_mask, positive_mask);
        
        if (seg.new_dart_pixel_ratio < DETECTION_MIN_NEW_DART_PIXEL_RATIO && seg.new_count > 0) {
            result.mask_quality *= 0.5;
        }
    }
    
    // Step 3: Shape filter
    motion_mask = shape_filter(motion_mask);
    
    // Step 3a: Blob distance chaining
    cv::Mat pre_chain_mask = motion_mask.clone();
    
    std::vector<std::vector<cv::Point>> contours_dist;
    cv::findContours(motion_mask.clone(), contours_dist, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
    
    if ((int)contours_dist.size() > 1) {
        struct CentroidInfo { double cx, cy; bool valid; };
        std::vector<CentroidInfo> centroids(contours_dist.size());
        for (size_t i = 0; i < contours_dist.size(); ++i) {
            cv::Moments m = cv::moments(contours_dist[i]);
            if (m.m00 > 0) {
                centroids[i] = {m.m10 / m.m00, m.m01 / m.m00, true};
            } else {
                centroids[i] = {0, 0, false};
            }
        }
        
        int largest_idx = 0;
        for (size_t i = 1; i < contours_dist.size(); ++i) {
            if (cv::contourArea(contours_dist[i]) > cv::contourArea(contours_dist[largest_idx]))
                largest_idx = (int)i;
        }
        
        std::set<int> chained = {largest_idx};
        bool changed = true;
        while (changed) {
            changed = false;
            for (size_t i = 0; i < contours_dist.size(); ++i) {
                if (chained.count((int)i) || !centroids[i].valid) continue;
                for (int j : chained) {
                    if (!centroids[j].valid) continue;
                    double dx = centroids[i].cx - centroids[j].cx;
                    double dy = centroids[i].cy - centroids[j].cy;
                    if (std::sqrt(dx*dx + dy*dy) <= blob_chain_dist) {
                        chained.insert((int)i);
                        changed = true;
                        break;
                    }
                }
            }
        }
        
        cv::Mat clean_mask = cv::Mat::zeros(motion_mask.size(), CV_8U);
        for (size_t i = 0; i < contours_dist.size(); ++i) {
            if (chained.count((int)i) || !centroids[i].valid)
                cv::drawContours(clean_mask, contours_dist, (int)i, cv::Scalar(255), -1);
        }
        motion_mask = clean_mask;
    }
    
    // Step 3c: Morphological closing to bridge barrel gaps
    cv::Mat pre_close_mask = motion_mask.clone();
    cv::Mat morph_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE,
        cv::Size(morph_close_k, morph_close_k));
    cv::morphologyEx(motion_mask, motion_mask, cv::MORPH_CLOSE, morph_kern);
    
    // Mask quality
    int mask_pixels = cv::countNonZero(motion_mask);
    if (mask_pixels > (int)mask_quality_thr) {
        result.mask_quality = std::min(1.0, (mask_quality_thr * 2.0 / 3.0) / mask_pixels);
    }
    result.mask_quality = std::max(0.1, result.mask_quality);
    
    // Step 4: Barrel-centric line detection
    std::optional<PcaLine> pca_line;
    std::optional<Point2f> flight_centroid;
    std::optional<BarrelInfo> barrel_info;
    
    auto flight = find_flight_blob(motion_mask, 80);
    if (flight) {
        flight_centroid = flight->centroid;
    } else {
        std::vector<cv::Point> pts;
        cv::findNonZero(motion_mask, pts);
        if (!pts.empty()) {
            double sx = 0, sy = 0;
            for (const auto& p : pts) { sx += p.x; sy += p.y; }
            flight_centroid = Point2f(sx / pts.size(), sy / pts.size());
        }
    }
    
    std::optional<double> ref_angle;
    if (flight_centroid) {
        double rdx = board_center.x - flight_centroid->x;
        double rdy = board_center.y - flight_centroid->y;
        double ref_len = std::sqrt(rdx*rdx + rdy*rdy);
        if (ref_len > 10)
            ref_angle = std::atan2(rdy, rdx);
    }
    
    if (mask_pixels > 50) {
        // === Width-profile barrel splitting ===
        cv::Mat barrel_mask;
        
        if (flight) {
            std::vector<cv::Point> dart_pts;
            cv::findNonZero(motion_mask, dart_pts);
            
            if ((int)dart_pts.size() > 100) {
                // === DUAL-AXIS BARREL SPLITTING (Feb 19, 2026) ===
                struct SplitResult {
                    cv::Mat mask;
                    double aspect;
                    double bcx, bcy, pvx, pvy;
                    int area;
                };
                
                auto try_axis = [&](bool rows) -> SplitResult {
                    SplitResult sr;
                    sr.aspect = 0; sr.area = 0;
                    
                    // Phase 3 (Change 6): Pre-bucket dart pixels by row or column
                    // instead of scanning all points per row/col line
                    std::map<int, std::pair<int,int>> axis_minmax; // pv -> (s_min, s_max)
                    for (const auto& pt : dart_pts) {
                        int pv = rows ? pt.y : pt.x;
                        int ss = rows ? pt.x : pt.y;
                        auto it = axis_minmax.find(pv);
                        if (it == axis_minmax.end()) {
                            axis_minmax[pv] = {ss, ss};
                        } else {
                            it->second.first = std::min(it->second.first, ss);
                            it->second.second = std::max(it->second.second, ss);
                        }
                    }
                    
                    // Build width profile from pre-bucketed data
                    std::vector<std::pair<int,int>> widths;
                    for (auto& [pv, mm] : axis_minmax) {
                        int w = mm.second - mm.first + 1;
                        widths.push_back({pv, w});
                    }
                    if (widths.empty()) return sr;
                    
                    int max_w = 0;
                    for (auto& [pv,w] : widths) max_w = std::max(max_w, w);
                    double thr = max_w * 0.5;
                    
                    double fc = rows ? flight->centroid.y : flight->centroid.x;
                    double bc = rows ? board_center.y : board_center.x;
                    bool reverse = (fc > bc);
                    
                    auto find_junc = [&](bool rev) -> int {
                        bool in_fl = false;
                        if (!rev) {
                            for (auto& [pv,w] : widths) {
                                if (w >= thr) in_fl = true;
                                else if (in_fl && w < thr && w > 0) return pv;
                            }
                        } else {
                            for (int i = (int)widths.size()-1; i >= 0; --i) {
                                if (widths[i].second >= thr) in_fl = true;
                                else if (in_fl && widths[i].second < thr && widths[i].second > 0) return widths[i].first;
                            }
                        }
                        return -1;
                    };
                    
                    int junc = find_junc(reverse);
                    if (junc < 0) junc = find_junc(!reverse);
                    if (junc < 0) return sr;
                    
                    sr.mask = motion_mask.clone();
                    if (rows) {
                        if (!reverse)
                            sr.mask(cv::Range(0, junc), cv::Range::all()) = 0;
                        else
                            sr.mask(cv::Range(junc+1, sr.mask.rows), cv::Range::all()) = 0;
                    } else {
                        if (!reverse)
                            sr.mask(cv::Range::all(), cv::Range(0, junc)) = 0;
                        else
                            sr.mask(cv::Range::all(), cv::Range(junc+1, sr.mask.cols)) = 0;
                    }
                    
                    sr.area = cv::countNonZero(sr.mask);
                    if (sr.area < 20) { sr.mask = cv::Mat(); sr.area = 0; return sr; }
                    
                    std::vector<cv::Point> bp;
                    cv::findNonZero(sr.mask, bp);
                    double bxs=0, bys=0;
                    for (auto& p : bp) { bxs += p.x; bys += p.y; }
                    sr.bcx = bxs/bp.size(); sr.bcy = bys/bp.size();
                    
                    double pvs = 0; int pvc = 0;
                    for (auto& p : bp) {
                        int pv = rows ? p.y : p.x;
                        if (pv == junc) { pvs += (rows ? p.x : p.y); pvc++; }
                    }
                    if (rows) { sr.pvx = pvc>0 ? pvs/pvc : sr.bcx; sr.pvy = (double)junc; }
                    else      { sr.pvx = (double)junc; sr.pvy = pvc>0 ? pvs/pvc : sr.bcy; }
                    
                    if ((int)bp.size() >= 5) {
                        cv::RotatedRect rr = cv::minAreaRect(bp);
                        double ls = std::max(rr.size.width, rr.size.height);
                        double ss = std::min(rr.size.width, rr.size.height) + 1.0;
                        sr.aspect = ls / ss;
                    }
                    return sr;
                };
                
                auto sr_row = try_axis(true);
                auto sr_col = try_axis(false);
                
                SplitResult* best = nullptr;
                if (sr_row.area > 0 && sr_col.area > 0)
                    best = (sr_row.aspect >= sr_col.aspect) ? &sr_row : &sr_col;
                else if (sr_row.area > 0) best = &sr_row;
                else if (sr_col.area > 0) best = &sr_col;
                
                if (best && best->aspect < 2.5) {
                    best = nullptr;
                }
                
                if (best) {
                    barrel_mask = best->mask;
                    barrel_info = BarrelInfo{
                        Point2f(best->bcx, best->bcy),
                        Point2f(best->pvx, best->pvy),
                        best->area
                    };
                    result.barrel_aspect_ratio = best->aspect;
                }
            }
        }
        
        // === Phase 4A: Erode barrel mask before fitting ===
        cv::Mat barrel_mask_eroded;
        if (!barrel_mask.empty()) {
            cv::Mat erode_kern = cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(3, 3));
            cv::erode(barrel_mask, barrel_mask_eroded, erode_kern);
            if (cv::countNonZero(barrel_mask_eroded) < 20) barrel_mask_eroded = barrel_mask;
        }

        // === Phase 4C: Edge-pair barrel detection (try FIRST before RANSAC) ===
        if (!barrel_mask.empty() && barrel_info) {
            cv::Mat gray_curr_ep, gray_prev_ep, gray_diff_ep;
            if (current_frame.channels() == 3)
                cv::cvtColor(current_frame, gray_curr_ep, cv::COLOR_BGR2GRAY);
            else gray_curr_ep = current_frame;
            if (previous_frame.channels() == 3)
                cv::cvtColor(previous_frame, gray_prev_ep, cv::COLOR_BGR2GRAY);
            else gray_prev_ep = previous_frame;
            cv::absdiff(gray_curr_ep, gray_prev_ep, gray_diff_ep);

            cv::Mat ep_edges;
            cv::Canny(gray_diff_ep, ep_edges, 30, 90);
            cv::Mat barrel_edges;
            cv::bitwise_and(ep_edges, barrel_mask, barrel_edges);

            std::vector<cv::Point> ep_pts;
            cv::findNonZero(barrel_edges, ep_pts);

            if ((int)ep_pts.size() >= 20) {
                // Rough barrel direction from centroid->pivot
                double ep_rvx = barrel_info->pivot.x - barrel_info->centroid.x;
                double ep_rvy = barrel_info->pivot.y - barrel_info->centroid.y;
                double ep_rlen = std::sqrt(ep_rvx*ep_rvx + ep_rvy*ep_rvy);
                if (ep_rlen < 5.0) {
                    std::vector<cv::Point> bp; cv::findNonZero(barrel_mask, bp);
                    if ((int)bp.size() > 10) {
                        std::vector<cv::Point2f> bpf(bp.begin(), bp.end());
                        cv::Vec4f lp; cv::fitLine(bpf, lp, cv::DIST_HUBER, 0, 0.01, 0.01);
                        ep_rvx = lp[0]; ep_rvy = lp[1]; ep_rlen = 1.0;
                    }
                }
                if (ep_rlen >= 1.0) {
                    if (ep_rlen != 1.0) { ep_rvx /= ep_rlen; ep_rvy /= ep_rlen; }
                    if (ep_rvy < 0) { ep_rvx = -ep_rvx; ep_rvy = -ep_rvy; }
                    double ep_pvx = -ep_rvy, ep_pvy = ep_rvx;
                    double ep_cx = barrel_info->centroid.x, ep_cy = barrel_info->centroid.y;

                    std::vector<cv::Point2f> left_pts, right_pts;
                    for (const auto& p : ep_pts) {
                        double d = (p.x - ep_cx) * ep_pvx + (p.y - ep_cy) * ep_pvy;
                        if (d < 0) left_pts.push_back(cv::Point2f((float)p.x, (float)p.y));
                        else       right_pts.push_back(cv::Point2f((float)p.x, (float)p.y));
                    }

                    if ((int)left_pts.size() >= 6 && (int)right_pts.size() >= 6) {
                        cv::Vec4f ll, rl;
                        cv::fitLine(left_pts, ll, cv::DIST_HUBER, 0, 0.01, 0.01);
                        cv::fitLine(right_pts, rl, cv::DIST_HUBER, 0, 0.01, 0.01);
                        double lvx=ll[0],lvy=ll[1], rvx=rl[0],rvy=rl[1];
                        if (lvx*ep_rvx+lvy*ep_rvy < 0) { lvx=-lvx; lvy=-lvy; }
                        if (rvx*ep_rvx+rvy*ep_rvy < 0) { rvx=-rvx; rvy=-rvy; }

                        double dot = lvx*rvx + lvy*rvy;
                        double ang_between = std::acos(std::min(1.0, std::abs(dot))) * 180.0 / CV_PI;

                        if (ang_between <= 15.0) {
                            double avx=(lvx+rvx)/2, avy=(lvy+rvy)/2;
                            double alen=std::sqrt(avx*avx+avy*avy);
                            if (alen>0) { avx/=alen; avy/=alen; }
                            double apvx=-avy, apvy=avx;
                            double edge_dist = std::abs((rl[2]-ll[2])*apvx + (rl[3]-ll[3])*apvy);

                            if (edge_dist >= 3.0*rs && edge_dist <= 25.0*rs) {
                                double fcx=(ll[2]+rl[2])/2, fcy=(ll[3]+rl[3])/2;
                                double fvx=avx, fvy=avy;
                                if (fvy<0) { fvx=-fvx; fvy=-fvy; }

                                bool accept=true;
                                if (ref_angle) {
                                    double la=std::atan2(fvy,fvx);
                                    double ad=std::abs(la - *ref_angle);
                                    if (ad>CV_PI) ad=2*CV_PI-ad;
                                    ad=std::min(ad, CV_PI-ad);
                                    if (ad > CV_PI*60.0/180.0) accept=false;
                                }
                                if (accept) {
                                    pca_line = PcaLine{fvx, fvy, fcx, fcy,
                                        (double)(left_pts.size()+right_pts.size()), "edge_pair"};
                                    result.ransac_inlier_ratio = 1.0;
                                    result.barrel_pixel_count = (int)ep_pts.size();
                                }
                            }
                        }
                    }
                }
            }
        }

        // === Barrel RANSAC line fitting ===
        if (!barrel_mask.empty() && barrel_info && !pca_line) {
            std::vector<cv::Point> barrel_pts;
            cv::findNonZero(barrel_mask_eroded, barrel_pts);

            if (barrel_pts.size() > 20) {
                double best_cost = 1e18;
                int best_inliers = 0;
                double best_vx = 0, best_vy = 0, best_cx = 0, best_cy = 0;
                int iterations = 150;
                double threshold = ransac_threshold;
                double T2 = threshold * threshold;

                std::mt19937 rng(42);
                std::uniform_int_distribution<int> dist(0, (int)barrel_pts.size() - 1);

                for (int iter = 0; iter < iterations; ++iter) {
                    int i1 = dist(rng), i2 = dist(rng);
                    if (i1 == i2) continue;

                    double dx = barrel_pts[i2].x - barrel_pts[i1].x;
                    double dy = barrel_pts[i2].y - barrel_pts[i1].y;
                    double len = std::sqrt(dx*dx + dy*dy);
                    if (len < ransac_min_pair) continue;  // Phase 3: scaled min pair distance

                    double nx = -dy / len, ny = dx / len;

                    double cost = 0;
                    int inliers = 0;
                    for (const auto& p : barrel_pts) {
                        double d = std::abs(nx * (p.x - barrel_pts[i1].x) + ny * (p.y - barrel_pts[i1].y));
                        cost += std::min(d * d, T2);
                        if (d <= threshold) inliers++;
                    }

                    if (cost < best_cost) {
                        best_cost = cost;
                        best_inliers = inliers;
                        best_vx = dx / len;
                        best_vy = dy / len;
                        std::vector<cv::Point2f> inlier_pts;
                        for (const auto& p : barrel_pts) {
                            double d = std::abs(nx * (p.x - barrel_pts[i1].x) + ny * (p.y - barrel_pts[i1].y));
                            if (d <= threshold) inlier_pts.push_back(cv::Point2f(p.x, p.y));
                        }
                        if (inlier_pts.size() > 5) {
                            cv::Vec4f lp;
                            cv::fitLine(inlier_pts, lp, cv::DIST_HUBER, 0, 0.01, 0.01);
                            best_vx = lp[0]; best_vy = lp[1];
                            best_cx = lp[2]; best_cy = lp[3];
                        }
                    }
                }

                // lo-RANSAC final refit (Phase 2)
                if (best_inliers > 5) {
                    double fnx = -best_vy, fny = best_vx;
                    std::vector<cv::Point2f> final_inliers;
                    for (const auto& p : barrel_pts) {
                        double d = std::abs(fnx * (p.x - best_cx) + fny * (p.y - best_cy));
                        if (d <= threshold) final_inliers.push_back(cv::Point2f(p.x, p.y));
                    }
                    if ((int)final_inliers.size() > 5) {
                        cv::Vec4f lp;
                        cv::fitLine(final_inliers, lp, cv::DIST_HUBER, 0, 0.01, 0.01);
                        double re_vx = lp[0], re_vy = lp[1], re_cx = lp[2], re_cy = lp[3];
                        double re_nx = -re_vy, re_ny = re_vx;
                        double re_cost = 0;
                        int re_inliers = 0;
                        for (const auto& p : barrel_pts) {
                            double d = std::abs(re_nx * (p.x - re_cx) + re_ny * (p.y - re_cy));
                            re_cost += std::min(d * d, T2);
                            if (d <= threshold) re_inliers++;
                        }
                        if (re_cost <= best_cost) {
                            best_vx = re_vx; best_vy = re_vy;
                            best_cx = re_cx; best_cy = re_cy;
                            best_cost = re_cost;
                            best_inliers = re_inliers;
                        }
                    }
                }

                double inlier_ratio = (double)best_inliers / barrel_pts.size();

                if (inlier_ratio >= 0.3) {
                    if (best_vy < 0) { best_vx = -best_vx; best_vy = -best_vy; }

                    // Phase 3 (Change 2): Angle tolerance 75 -> 60 degrees
                    bool accept = true;
                    if (ref_angle) {
                        double line_angle = std::atan2(best_vy, best_vx);
                        double angle_diff = std::abs(line_angle - *ref_angle);
                        if (angle_diff > CV_PI) angle_diff = 2 * CV_PI - angle_diff;
                        angle_diff = std::min(angle_diff, CV_PI - angle_diff);
                        if (angle_diff > CV_PI * 60.0 / 180.0) accept = false;
                    }

                    if (accept) {
                        pca_line = PcaLine{best_vx, best_vy, best_cx, best_cy,
                                           (double)best_inliers, "barrel_ransac"};
                        result.ransac_inlier_ratio = inlier_ratio;
                        result.barrel_pixel_count = (int)barrel_pts.size();
                    }
                }
            }
            
            // Fallback: fitLine on barrel pixels
            if (!pca_line) {
                std::vector<cv::Point> b_pts;
                cv::findNonZero(barrel_mask, b_pts);
                if ((int)b_pts.size() > 10) {
                    std::vector<cv::Point2f> pts_f(b_pts.begin(), b_pts.end());
                    cv::Vec4f line_params;
                    cv::fitLine(pts_f, line_params, cv::DIST_HUBER, 0, 0.01, 0.01);
                    double vx = line_params[0], vy = line_params[1];
                    if (vy < 0) { vx = -vx; vy = -vy; }
                    pca_line = PcaLine{vx, vy, barrel_info->pivot.x, barrel_info->pivot.y,
                                       (double)b_pts.size(), "barrel_fitline"};
                }
            }
        }
        
        // === Barrel-width-profiled fitLine (between RANSAC and Hough fallback) ===
        if (!pca_line) {
            cv::Mat skel_bw;
            zhangSuenThinning(motion_mask, skel_bw);
            
            std::vector<cv::Point> skel_pts;
            cv::findNonZero(skel_bw, skel_pts);
            
            if ((int)skel_pts.size() > 20) {
                std::set<std::pair<int,int>> skel_set;
                for (const auto& p : skel_pts)
                    skel_set.insert({p.y, p.x});
                
                std::vector<cv::Point> endpoints;
                for (const auto& p : skel_pts) {
                    int n = 0;
                    for (int dy = -1; dy <= 1; ++dy)
                        for (int dx = -1; dx <= 1; ++dx)
                            if ((dy || dx) && skel_set.count({p.y+dy, p.x+dx}))
                                ++n;
                    if (n == 1) endpoints.push_back(p);
                }
                
                std::vector<cv::Point> best_path;
                
                for (const auto& start : endpoints) {
                    std::map<std::pair<int,int>, int> dist_map;
                    std::map<std::pair<int,int>, std::pair<int,int>> parent;
                    std::queue<std::pair<int,int>> bfs_q;
                    std::pair<int,int> sk(start.y, start.x);
                    dist_map[sk] = 0;
                    parent[sk] = std::make_pair(-1, -1);
                    bfs_q.push(sk);
                    std::pair<int,int> farthest = sk;
                    int max_dist = 0;
                    
                    while (!bfs_q.empty()) {
                        auto cur_node = bfs_q.front(); bfs_q.pop();
                        int cy = cur_node.first, cx = cur_node.second;
                        int d = dist_map[std::make_pair(cy, cx)];
                        if (d > max_dist) { max_dist = d; farthest = std::make_pair(cy, cx); }
                        
                        for (int dy = -1; dy <= 1; ++dy)
                            for (int dx = -1; dx <= 1; ++dx) {
                                if (!dy && !dx) continue;
                                std::pair<int,int> nk(cy+dy, cx+dx);
                                if (skel_set.count(nk) && !dist_map.count(nk)) {
                                    dist_map[nk] = d + 1;
                                    parent[nk] = std::make_pair(cy, cx);
                                    bfs_q.push(nk);
                                }
                            }
                    }
                    
                    if (max_dist > (int)best_path.size()) {
                        std::vector<cv::Point> path_pts;
                        auto cur_trace = farthest;
                        while (cur_trace.first >= 0) {
                            path_pts.push_back(cv::Point(cur_trace.second, cur_trace.first));
                            cur_trace = parent[cur_trace];
                        }
                        if ((int)path_pts.size() > (int)best_path.size())
                            best_path = path_pts;
                    }
                }
                
                // Measure perpendicular width along path
                if ((int)best_path.size() > 20) {
                    const int window = 15;
                    int width_thresh = (int)barrel_width_max; // Phase 3: scaled
                    std::vector<cv::Point2f> barrel_pts_bw;
                    int h = motion_mask.rows, w = motion_mask.cols;
                    
                    // Phase 3 (Change 6): Pre-bucket mask pixels by row for fast width lookup
                    std::map<int, std::pair<int,int>> row_minmax;
                    {
                        std::vector<cv::Point> all_mask_pts;
                        cv::findNonZero(motion_mask, all_mask_pts);
                        for (const auto& p : all_mask_pts) {
                            auto it = row_minmax.find(p.y);
                            if (it == row_minmax.end()) row_minmax[p.y] = {p.x, p.x};
                            else { it->second.first = std::min(it->second.first, p.x); it->second.second = std::max(it->second.second, p.x); }
                        }
                    }
                    
                    for (int i = 0; i < (int)best_path.size(); ++i) {
                        int x0 = best_path[i].x, y0 = best_path[i].y;
                        
                        int i0 = std::max(0, i - window);
                        int i1 = std::min((int)best_path.size() - 1, i + window);
                        double ldx = best_path[i1].x - best_path[i0].x;
                        double ldy = best_path[i1].y - best_path[i0].y;
                        double ll = std::sqrt(ldx*ldx + ldy*ldy);
                        if (ll < 1) continue;
                        
                        double px = -ldy / ll, py = ldx / ll;
                        
                        int count = 1;
                        for (int sign : {1, -1}) {
                            for (int t = 1; t < 80; ++t) {
                                int nx = (int)std::round(x0 + sign * px * t);
                                int ny = (int)std::round(y0 + sign * py * t);
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h || 
                                    motion_mask.at<uchar>(ny, nx) == 0) break;
                                ++count;
                            }
                        }
                        
                        if (count < width_thresh) {
                            barrel_pts_bw.push_back(cv::Point2f((float)x0, (float)y0));
                        }
                    }
                    
                    if ((int)barrel_pts_bw.size() > 15) {
                        cv::Vec4f lp;
                        cv::fitLine(barrel_pts_bw, lp, cv::DIST_HUBER, 0, 0.01, 0.01);
                        double bvx = lp[0], bvy = lp[1], bcx = lp[2], bcy = lp[3];
                        if (bvy < 0) { bvx = -bvx; bvy = -bvy; }
                        
                        // Phase 3 (Change 2): Angle tolerance 75 -> 60 degrees
                        bool accept = true;
                        if (ref_angle) {
                            double la = std::atan2(bvy, bvx);
                            double ad = std::abs(la - *ref_angle);
                            if (ad > CV_PI) ad = 2*CV_PI - ad;
                            ad = std::min(ad, CV_PI - ad);
                            if (ad > CV_PI * 60.0 / 180.0) accept = false;
                        }
                        
                        if (accept) {
                            pca_line = PcaLine{bvx, bvy, bcx, bcy,
                                               (double)barrel_pts_bw.size(), "barrel_width_fit"};
                        }
                    }
                }
            }
        }
        
        // === Fallback: Full mask skeleton + Hough ===
        if (!pca_line) {
            std::vector<std::vector<cv::Point>> contours_skel;
            cv::findContours(motion_mask.clone(), contours_skel, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
            
            if (!contours_skel.empty()) {
                cv::Mat skel;
                zhangSuenThinning(motion_mask, skel);
                
                std::vector<cv::Vec4i> hough_lines;
                cv::HoughLinesP(skel, hough_lines, 1, CV_PI / 1800, 12, 15, 8);
                
                if (!hough_lines.empty()) {
                    std::vector<cv::Point> mask_pts;
                    cv::findNonZero(motion_mask, mask_pts);
                    double tip_center_x = 0, tip_center_y = 0;
                    bool has_tip_center = false;
                    
                    if (!mask_pts.empty()) {
                        std::vector<int> ys;
                        for (const auto& p : mask_pts) ys.push_back(p.y);
                        std::sort(ys.begin(), ys.end());
                        int y_thresh = ys[(int)(ys.size() * 0.9)];
                        double sx = 0, sy = 0; int cnt = 0;
                        for (const auto& p : mask_pts) {
                            if (p.y >= y_thresh) { sx += p.x; sy += p.y; ++cnt; }
                        }
                        if (cnt > 0) {
                            tip_center_x = sx / cnt;
                            tip_center_y = sy / cnt;
                            has_tip_center = true;
                        }
                    }
                    
                    struct ScoredLine { cv::Vec4i line; double length, angle, score; };
                    std::vector<ScoredLine> scored;
                    
                    for (const auto& hl : hough_lines) {
                        double dx = hl[2] - hl[0], dy = hl[3] - hl[1];
                        double len = std::sqrt(dx*dx + dy*dy);
                        double a = std::atan2(dy, dx);
                        double angle_score = 0.5;
                        if (ref_angle) {
                            double diff_a = std::abs(a - *ref_angle);
                            diff_a = std::min(diff_a, CV_PI - diff_a);
                            angle_score = std::cos(diff_a);
                        }
                        double tip_prox_score = 1.0;
                        if (has_tip_center) {
                            double line_len = len;
                            if (line_len > 0) {
                                double perp = std::abs(dy * tip_center_x - dx * tip_center_y
                                    + (double)hl[2] * hl[1] - (double)hl[3] * hl[0]) / line_len;
                                tip_prox_score = std::max(0.1, 1.0 - perp / 100.0);
                            }
                        }
                        double score = len * std::max(0.5, 0.5 + 0.5 * angle_score) * tip_prox_score;
                        scored.push_back({hl, len, a, score});
                    }
                    
                    std::sort(scored.begin(), scored.end(),
                        [](const auto& a, const auto& b) { return a.score > b.score; });
                    
                    double best_angle2 = scored[0].angle;
                    double avg_vx2 = 0, avg_vy2 = 0, total_w2 = 0;
                    double avg_cx = 0, avg_cy = 0, max_len2 = 0;
                    int n_avg2 = 0;
                    for (int si = 0; si < (int)scored.size() && n_avg2 < 3; ++si) {
                        double adiff = std::abs(scored[si].angle - best_angle2);
                        adiff = std::min(adiff, CV_PI - adiff);
                        if (adiff > CV_PI / 6.0) continue;
                        double dx_i = scored[si].line[2] - scored[si].line[0];
                        double dy_i = scored[si].line[3] - scored[si].line[1];
                        double n_i = std::sqrt(dx_i*dx_i + dy_i*dy_i);
                        if (n_i <= 0) continue;
                        double uvx = dx_i / n_i, uvy = dy_i / n_i;
                        double dot_best = uvx * std::cos(best_angle2) + uvy * std::sin(best_angle2);
                        if (dot_best < 0) { uvx = -uvx; uvy = -uvy; }
                        avg_vx2 += uvx * scored[si].score;
                        avg_vy2 += uvy * scored[si].score;
                        avg_cx += ((scored[si].line[0] + scored[si].line[2]) / 2.0) * scored[si].score;
                        avg_cy += ((scored[si].line[1] + scored[si].line[3]) / 2.0) * scored[si].score;
                        total_w2 += scored[si].score;
                        max_len2 = std::max(max_len2, scored[si].length);
                        ++n_avg2;
                    }
                    if (total_w2 > 0) {
                        avg_vx2 /= total_w2; avg_vy2 /= total_w2;
                        avg_cx /= total_w2; avg_cy /= total_w2;
                    }
                    double norm2 = std::sqrt(avg_vx2*avg_vx2 + avg_vy2*avg_vy2);
                    if (norm2 > 0) {
                        double vx = avg_vx2 / norm2, vy = avg_vy2 / norm2;
                        if (vy < 0) { vx = -vx; vy = -vy; }
                        pca_line = PcaLine{vx, vy, avg_cx, avg_cy,
                            max_len2, "skeleton_hough_fallback"};
                    }
                }
                
                if (!pca_line && !contours_skel.empty()) {
                    auto& largest = *std::max_element(contours_skel.begin(), contours_skel.end(),
                        [](const auto& a, const auto& b) { return cv::contourArea(a) < cv::contourArea(b); });
                    if ((int)largest.size() > 10) {
                        std::vector<cv::Point2f> pts_f(largest.begin(), largest.end());
                        cv::Vec4f lp;
                        cv::fitLine(pts_f, lp, cv::DIST_HUBER, 0, 0.01, 0.01);
                        double vx = lp[0], vy = lp[1];
                        if (vy < 0) { vx = -vx; vy = -vy; }
                        pca_line = PcaLine{vx, vy, (double)lp[2], (double)lp[3],
                                           (double)largest.size(), "fitline_huber_fallback"};
                    }
                }
                
                if (!pca_line) {
                    std::vector<cv::Point> pts;
                    cv::findNonZero(motion_mask, pts);
                    if ((int)pts.size() > 10) {
                        cv::Mat data((int)pts.size(), 2, CV_64F);
                        for (int i = 0; i < (int)pts.size(); ++i) {
                            data.at<double>(i, 0) = pts[i].x;
                            data.at<double>(i, 1) = pts[i].y;
                        }
                        cv::PCA pca(data, cv::Mat(), cv::PCA::DATA_AS_ROW);
                        double vx = pca.eigenvectors.at<double>(0, 0);
                        double vy = pca.eigenvectors.at<double>(0, 1);
                        if (vy < 0) { vx = -vx; vy = -vy; }
                        pca_line = PcaLine{vx, vy,
                            pca.mean.at<double>(0, 0), pca.mean.at<double>(0, 1),
                            pca.eigenvalues.at<double>(0, 0) / (pca.eigenvalues.at<double>(1, 0) + 1e-6),
                            "full_pca_fallback"};
                    }
                }
            }
        }
    }
    
    // Step 4b: Line-guided blob absorption
    if (pca_line && !pre_chain_mask.empty()) {
        cv::Mat filtered_out;
        cv::Mat not_motion;
        cv::bitwise_not(motion_mask, not_motion);
        cv::bitwise_and(pre_chain_mask, not_motion, filtered_out);
        
        std::vector<cv::Point> filt_pts;
        cv::findNonZero(filtered_out, filt_pts);
        
        if (!filt_pts.empty()) {
            std::vector<cv::Point> mask_pts;
            cv::findNonZero(motion_mask, mask_pts);
            
            if (!mask_pts.empty()) {
                double mask_along_min = 1e9, mask_along_max = -1e9;
                for (const auto& p : mask_pts) {
                    double along = (p.x - pca_line->x0) * pca_line->vx +
                                   (p.y - pca_line->y0) * pca_line->vy;
                    mask_along_min = std::min(mask_along_min, along);
                    mask_along_max = std::max(mask_along_max, along);
                }
                
                for (const auto& p : filt_pts) {
                    double dx = p.x - pca_line->x0;
                    double dy = p.y - pca_line->y0;
                    double perp = std::abs(dx * pca_line->vy - dy * pca_line->vx);
                    double along = dx * pca_line->vx + dy * pca_line->vy;
                    
                    if (perp <= LINE_ABSORB_PERP_DIST &&
                        along >= mask_along_min - LINE_ABSORB_EXTEND_LIMIT &&
                        along <= mask_along_max + LINE_ABSORB_EXTEND_LIMIT) {
                        motion_mask.at<uchar>(p.y, p.x) = 255;
                    }
                }
            }
        }
    }
    
    // Step 5: PCA blob chain tip detection
    std::optional<Point2f> tip;
    std::string tip_method = "none";
    double dart_length = 0.0;
    
    if (pca_line) {
        int h = motion_mask.rows, w = motion_mask.cols;
        
        cv::Mat walk_mask = (!pre_chain_mask.empty()) ? pre_chain_mask : motion_mask;
        cv::Mat labeled;
        int n_labels = cv::connectedComponents(walk_mask, labeled, 8, CV_32S);
        
        double walk_vx = pca_line->vx, walk_vy = pca_line->vy;
        if (walk_vy < 0) { walk_vx = -walk_vx; walk_vy = -walk_vy; }
        
        double perp_vx = -walk_vy, perp_vy = walk_vx;
        
        std::set<int> visited_labels;
        double current_x = pca_line->x0, current_y = pca_line->y0;
        std::optional<Point2f> last_blob_tip;
        int last_blob_label = -1;
        
        int step = 0;
        while (step < PCA_MAX_WALK) {
            int px = (int)std::round(current_x + walk_vx * step);
            int py = (int)std::round(current_y + walk_vy * step);
            
            if (px < 0 || px >= w || py < 0 || py >= h) break;
            
            int label = labeled.at<int>(py, px);
            
            if (label == 0) {
                for (int po = 1; po <= PCA_PERP_TOLERANCE; ++po) {
                    for (int sign : {1, -1}) {
                        int cx = (int)std::round(px + perp_vx * po * sign);
                        int cy = (int)std::round(py + perp_vy * po * sign);
                        if (cx >= 0 && cx < w && cy >= 0 && cy < h) {
                            int lbl = labeled.at<int>(cy, cx);
                            if (lbl > 0 && !visited_labels.count(lbl)) {
                                label = lbl;
                                break;
                            }
                        }
                    }
                    if (label > 0) break;
                }
            }
            
            if (label > 0 && !visited_labels.count(label)) {
                visited_labels.insert(label);
                last_blob_label = label;
                
                std::vector<cv::Point> blob_pts;
                for (int r = 0; r < h; ++r)
                    for (int c = 0; c < w; ++c)
                        if (labeled.at<int>(r, c) == label)
                            blob_pts.push_back(cv::Point(c, r));
                
                double bx = 0, by = 0;
                for (const auto& p : blob_pts) { bx += p.x; by += p.y; }
                bx /= blob_pts.size(); by /= blob_pts.size();
                
                int max_y_idx = 0;
                for (size_t i = 1; i < blob_pts.size(); ++i)
                    if (blob_pts[i].y > blob_pts[max_y_idx].y) max_y_idx = (int)i;
                last_blob_tip = Point2f(blob_pts[max_y_idx].x, blob_pts[max_y_idx].y);
                
                current_x = bx; current_y = by;
                
                double max_along = 0;
                for (const auto& p : blob_pts) {
                    double along = (p.x - current_x) * walk_vx + (p.y - current_y) * walk_vy;
                    max_along = std::max(max_along, along);
                }
                step = (int)max_along + 1;
                continue;
            } else if (label == 0 && last_blob_label >= 0) {
                int gap_start = step;
                bool found = false;
                while (step < gap_start + PCA_GAP_TOLERANCE) {
                    int gx = (int)std::round(current_x + walk_vx * step);
                    int gy = (int)std::round(current_y + walk_vy * step);
                    if (gx < 0 || gx >= w || gy < 0 || gy >= h) break;
                    if (labeled.at<int>(gy, gx) > 0) { found = true; break; }
                    ++step;
                }
                if (!found) break;
                continue;
            }
            
            ++step;
        }
        
        if (last_blob_tip) {
            tip = *last_blob_tip;
            tip_method = "pca_blob_chain";
            if (flight_centroid) {
                double dx = tip->x - flight_centroid->x;
                double dy = tip->y - flight_centroid->y;
                dart_length = std::sqrt(dx*dx + dy*dy);
            }
        } else {
            std::optional<Point2f> fwd_last, bwd_last;
            for (int s = 0; s < 500; ++s) {
                int px = (int)std::round(pca_line->x0 + pca_line->vx * s);
                int py = (int)std::round(pca_line->y0 + pca_line->vy * s);
                if (px >= 0 && px < w && py >= 0 && py < h) {
                    if (motion_mask.at<uchar>(py, px) > 0) fwd_last = Point2f(px, py);
                } else if (fwd_last) break;
            }
            for (int s = 0; s < 500; ++s) {
                int px = (int)std::round(pca_line->x0 - pca_line->vx * s);
                int py = (int)std::round(pca_line->y0 - pca_line->vy * s);
                if (px >= 0 && px < w && py >= 0 && py < h) {
                    if (motion_mask.at<uchar>(py, px) > 0) bwd_last = Point2f(px, py);
                } else if (bwd_last) break;
            }
            if (fwd_last && bwd_last)
                tip = (fwd_last->y >= bwd_last->y) ? fwd_last : bwd_last;
            else if (fwd_last) tip = fwd_last;
            else if (bwd_last) tip = bwd_last;
            if (tip) tip_method = "line_walk_fallback";
        }
    }
    
    // Fallback: highest Y pixel
    if (!tip && mask_pixels > 200) {
        std::vector<cv::Point> pts;
        cv::findNonZero(motion_mask, pts);
        if (!pts.empty()) {
            auto it = std::max_element(pts.begin(), pts.end(),
                [](const auto& a, const auto& b) { return a.y < b.y; });
            tip = Point2f(it->x, it->y);
            tip_method = "highest_y_fallback";
        }
    }
    
    if (!tip) return result;
    
    // Sub-pixel refinement
    cv::Mat gray;
    if (current_frame.channels() == 3)
        cv::cvtColor(current_frame, gray, cv::COLOR_BGR2GRAY);
    else gray = current_frame;
    if (pca_line) {
        *tip = refine_tip_subpixel(*tip, gray, motion_mask, 10, pca_line->vx, pca_line->vy);
    } else {
        *tip = refine_tip_subpixel(*tip, gray, motion_mask);
    }
    
    // Compute line and quality
    double view_quality = 0.3;
    if (flight_centroid) {
        double dx = tip->x - flight_centroid->x;
        double dy = tip->y - flight_centroid->y;
        dart_length = std::sqrt(dx*dx + dy*dy);
        view_quality = std::min(1.0, dart_length / dart_length_min);
    }
    
    result.tip = tip;
    result.confidence = 0.8;
    result.pca_line = pca_line;
    result.dart_length = dart_length;
    result.method = tip_method;
    result.view_quality = view_quality;
    result.motion_mask = motion_mask;
    
    return result;
}


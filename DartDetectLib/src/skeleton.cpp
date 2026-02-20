/**
 * skeleton.cpp - Skeleton/Hough detection, barrel-centric detection, PCA blob chain tip
 * 
 * Ported from Python: skeleton_detection.py detect_dart()
 */
#include "dart_detect_internal.h"
// Zhang-Suen thinning (replaces cv::ximgproc::thinning to avoid contrib dependency)
namespace {
void zhangSuenThinning(const cv::Mat& src, cv::Mat& dst) {
    src.copyTo(dst);
    dst /= 255;  // Work with 0/1 values
    
    cv::Mat prev = cv::Mat::zeros(dst.size(), CV_8UC1);
    cv::Mat marker;
    
    while (true) {
        dst.copyTo(prev);
        
        // Sub-iteration 1
        marker = cv::Mat::zeros(dst.size(), CV_8UC1);
        for (int i = 1; i < dst.rows - 1; i++) {
            for (int j = 1; j < dst.cols - 1; j++) {
                if (dst.at<uchar>(i, j) != 1) continue;
                
                uchar p2 = dst.at<uchar>(i-1, j);
                uchar p3 = dst.at<uchar>(i-1, j+1);
                uchar p4 = dst.at<uchar>(i, j+1);
                uchar p5 = dst.at<uchar>(i+1, j+1);
                uchar p6 = dst.at<uchar>(i+1, j);
                uchar p7 = dst.at<uchar>(i+1, j-1);
                uchar p8 = dst.at<uchar>(i, j-1);
                uchar p9 = dst.at<uchar>(i-1, j-1);
                
                int B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                if (B < 2 || B > 6) continue;
                
                int A = (p2==0 && p3==1) + (p3==0 && p4==1) + (p4==0 && p5==1) +
                         (p5==0 && p6==1) + (p6==0 && p7==1) + (p7==0 && p8==1) +
                         (p8==0 && p9==1) + (p9==0 && p2==1);
                if (A != 1) continue;
                
                if (p2 * p4 * p6 != 0) continue;
                if (p4 * p6 * p8 != 0) continue;
                
                marker.at<uchar>(i, j) = 1;
            }
        }
        dst -= marker;
        
        // Sub-iteration 2
        marker = cv::Mat::zeros(dst.size(), CV_8UC1);
        for (int i = 1; i < dst.rows - 1; i++) {
            for (int j = 1; j < dst.cols - 1; j++) {
                if (dst.at<uchar>(i, j) != 1) continue;
                
                uchar p2 = dst.at<uchar>(i-1, j);
                uchar p3 = dst.at<uchar>(i-1, j+1);
                uchar p4 = dst.at<uchar>(i, j+1);
                uchar p5 = dst.at<uchar>(i+1, j+1);
                uchar p6 = dst.at<uchar>(i+1, j);
                uchar p7 = dst.at<uchar>(i+1, j-1);
                uchar p8 = dst.at<uchar>(i, j-1);
                uchar p9 = dst.at<uchar>(i-1, j-1);
                
                int B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                if (B < 2 || B > 6) continue;
                
                int A = (p2==0 && p3==1) + (p3==0 && p4==1) + (p4==0 && p5==1) +
                         (p5==0 && p6==1) + (p6==0 && p7==1) + (p7==0 && p8==1) +
                         (p8==0 && p9==1) + (p9==0 && p2==1);
                if (A != 1) continue;
                
                if (p2 * p4 * p8 != 0) continue;
                if (p2 * p6 * p8 != 0) continue;
                
                marker.at<uchar>(i, j) = 1;
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
static Point2f refine_tip_subpixel(Point2f tip, const cv::Mat& gray, const cv::Mat& mask, int roi_size = 10)
{
    int tx = (int)tip.x, ty = (int)tip.y;
    int h = gray.rows, w = gray.cols;
    int x1 = std::max(0, tx - roi_size), y1 = std::max(0, ty - roi_size);
    int x2 = std::min(w, tx + roi_size), y2 = std::min(h, ty + roi_size);
    if (x2 - x1 < 5 || y2 - y1 < 5) return tip;
    
    cv::Mat roi_gray = gray(cv::Range(y1, y2), cv::Range(x1, x2));
    cv::Mat roi_mask = mask(cv::Range(y1, y2), cv::Range(x1, x2));
    
    cv::Mat edges;
    cv::Canny(roi_gray, edges, 30, 100);
    cv::bitwise_and(edges, roi_mask, edges);
    
    std::vector<cv::Point> pts;
    cv::findNonZero(edges, pts);
    if ((int)pts.size() < 3) return tip;
    
    double min_dist = 1e9;
    cv::Point best = pts[0];
    for (const auto& p : pts) {
        double dx = (p.x + x1) - tip.x;
        double dy = (p.y + y1) - tip.y;
        double d = std::sqrt(dx * dx + dy * dy);
        if (d < min_dist) { min_dist = d; best = p; }
    }
    
    if (min_dist < roi_size)
        return Point2f(best.x + x1, best.y + y1);
    return tip;
}

// ============================================================================
// Main detection function
// ============================================================================

DetectionResult detect_dart(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    Point2f board_center,
    const std::vector<cv::Mat>& prev_dart_masks,
    int diff_threshold)
{
    DetectionResult result;
    
    // Step 1: Motion mask
    auto mmr = compute_motion_mask(current_frame, previous_frame, 5, diff_threshold);
    cv::Mat motion_mask = mmr.mask;
    cv::Mat positive_mask = mmr.positive_mask;
    
    // Step 2: Pixel segmentation for dart 2+
    if (!prev_dart_masks.empty()) {
        auto seg = compute_pixel_segmentation(
            current_frame, previous_frame, prev_dart_masks, diff_threshold, 5);
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
        // Compute centroids
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
        
        // Find largest contour as seed
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
                    if (std::sqrt(dx*dx + dy*dy) <= BLOB_CHAIN_DIST) {
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
        cv::Size(MORPH_CLOSE_KERNEL_SIZE, MORPH_CLOSE_KERNEL_SIZE));
    cv::morphologyEx(motion_mask, motion_mask, cv::MORPH_CLOSE, morph_kern);
    
    // Mask quality
    int mask_pixels = cv::countNonZero(motion_mask);
    if (mask_pixels > 12000) {
        result.mask_quality = std::min(1.0, 8000.0 / mask_pixels);
    }
    result.mask_quality = std::max(0.1, result.mask_quality);
    
    // Step 4: Barrel-centric line detection
    std::optional<PcaLine> pca_line;
    std::optional<Point2f> flight_centroid;
    std::optional<BarrelInfo> barrel_info;
    
    // Find flight blob
    auto flight = find_flight_blob(motion_mask, 80);
    if (flight) {
        flight_centroid = flight->centroid;
    } else {
        // Fallback: mean of all mask pixels
        std::vector<cv::Point> pts;
        cv::findNonZero(motion_mask, pts);
        if (!pts.empty()) {
            double sx = 0, sy = 0;
            for (const auto& p : pts) { sx += p.x; sy += p.y; }
            flight_centroid = Point2f(sx / pts.size(), sy / pts.size());
        }
    }
    
    // Reference direction: flight toward board center
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
                // Dual-axis barrel splitting: try row-based and column-based,
                // pick whichever yields a more elongated (better) barrel.
                
                struct SplitResult {
                    cv::Mat mask;
                    double aspect;
                    double bcx, bcy, pvx, pvy;
                    int area;
                };
                
                auto try_axis = [&](bool rows) -> SplitResult {
                    SplitResult sr;
                    sr.aspect = 0; sr.area = 0;
                    
                    int p_min = rows ? dart_pts[0].y : dart_pts[0].x;
                    int p_max = p_min;
                    for (const auto& pt : dart_pts) {
                        int pv = rows ? pt.y : pt.x;
                        p_min = std::min(p_min, pv);
                        p_max = std::max(p_max, pv);
                    }
                    
                    // Width profile along primary axis
                    std::vector<std::pair<int,int>> widths;
                    for (int pv = p_min; pv <= p_max; ++pv) {
                        int s_min = (int)1e6, s_max = -1;
                        for (const auto& pt : dart_pts) {
                            int pp = rows ? pt.y : pt.x;
                            int ss = rows ? pt.x : pt.y;
                            if (pp == pv) { s_min = std::min(s_min, ss); s_max = std::max(s_max, ss); }
                        }
                        int w = (s_max >= 0) ? (s_max - s_min + 1) : 0;
                        widths.push_back({pv, w});
                    }
                    if (widths.empty()) return sr;
                    
                    int max_w = 0;
                    for (auto& [pv,w] : widths) max_w = std::max(max_w, w);
                    double thr = max_w * 0.5;
                    
                    // Determine scan direction from flight toward board
                    double fc = rows ? flight->centroid.y : flight->centroid.x;
                    double bc = rows ? board_center.y : board_center.x;
                    bool reverse = (fc > bc);
                    
                    // Find junction (wide->narrow transition)
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
                    
                    // Build barrel mask
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
                    
                    // Pivot at junction edge
                    double pvs = 0; int pvc = 0;
                    for (auto& p : bp) {
                        int pv = rows ? p.y : p.x;
                        if (pv == junc) { pvs += (rows ? p.x : p.y); pvc++; }
                    }
                    if (rows) { sr.pvx = pvc>0 ? pvs/pvc : sr.bcx; sr.pvy = (double)junc; }
                    else      { sr.pvx = (double)junc; sr.pvy = pvc>0 ? pvs/pvc : sr.bcy; }
                    
                    // Aspect ratio
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
                
                // Pick better barrel (higher aspect = more elongated)
                SplitResult* best = nullptr;
                if (sr_row.area > 0 && sr_col.area > 0)
                    best = (sr_row.aspect >= sr_col.aspect) ? &sr_row : &sr_col;
                else if (sr_row.area > 0) best = &sr_row;
                else if (sr_col.area > 0) best = &sr_col;
                
                if (best) {
                    barrel_mask = best->mask;
                    barrel_info = BarrelInfo{
                        Point2f(best->bcx, best->bcy),
                        Point2f(best->pvx, best->pvy),
                        best->area
                    };
                }
            }
        }
        
        // === Barrel skeleton + Hough ===
        if (!barrel_mask.empty() && barrel_info) {
            cv::Mat skel;
            zhangSuenThinning(barrel_mask, skel);
            
            std::vector<cv::Vec4i> hough_lines;
            cv::HoughLinesP(skel, hough_lines, 1, CV_PI / 180, 8, 10, 5);
            
            if (!hough_lines.empty()) {
                // Score lines: length * angle alignment
                struct ScoredLine { cv::Vec4i line; double length, score, angle; };
                std::vector<ScoredLine> scored;
                
                for (const auto& hl : hough_lines) {
                    double dx = hl[2] - hl[0], dy = hl[3] - hl[1];
                    double len = std::sqrt(dx*dx + dy*dy);
                    double a = std::atan2(dy, dx);
                    double angle_score = 0.5;
                    if (ref_angle) {
                        double diff_a = std::abs(a - *ref_angle);
                        diff_a = std::min(diff_a, CV_PI - diff_a);
                        angle_score = std::max(0.5, 0.5 + 0.5 * std::cos(diff_a));
                    }
                    scored.push_back({hl, len, len * angle_score, a});
                }
                
                std::sort(scored.begin(), scored.end(),
                    [](const auto& a, const auto& b) { return a.score > b.score; });
                
                // Average top-N Hough lines (up to 3) within 30 deg of best
                double best_angle = scored[0].angle;
                double avg_vx = 0, avg_vy = 0, total_weight = 0;
                double max_len = 0;
                int n_avg = 0;
                for (int si = 0; si < (int)scored.size() && n_avg < 3; ++si) {
                    double adiff = std::abs(scored[si].angle - best_angle);
                    adiff = std::min(adiff, CV_PI - adiff);
                    if (adiff > CV_PI / 6.0) continue;  // > 30 deg
                    double dx_i = scored[si].line[2] - scored[si].line[0];
                    double dy_i = scored[si].line[3] - scored[si].line[1];
                    double n_i = std::sqrt(dx_i*dx_i + dy_i*dy_i);
                    if (n_i <= 0) continue;
                    double uvx = dx_i / n_i, uvy = dy_i / n_i;
                    // Align direction with best line
                    double dot_best = uvx * std::cos(best_angle) + uvy * std::sin(best_angle);
                    if (dot_best < 0) { uvx = -uvx; uvy = -uvy; }
                    avg_vx += uvx * scored[si].score;
                    avg_vy += uvy * scored[si].score;
                    total_weight += scored[si].score;
                    max_len = std::max(max_len, scored[si].length);
                    ++n_avg;
                }
                if (total_weight > 0) { avg_vx /= total_weight; avg_vy /= total_weight; }
                double norm = std::sqrt(avg_vx*avg_vx + avg_vy*avg_vy);
                if (norm > 0) {
                    double vx = avg_vx / norm, vy = avg_vy / norm;
                    if (vy < 0) { vx = -vx; vy = -vy; }
                    
                    bool accept = true;
                    if (ref_angle) {
                        double line_angle = std::atan2(vy, vx);
                        double angle_diff = std::abs(line_angle - *ref_angle);
                        if (angle_diff > CV_PI) angle_diff = 2 * CV_PI - angle_diff;
                        angle_diff = std::min(angle_diff, CV_PI - angle_diff);
                        if (angle_diff > CV_PI * 5.0 / 12.0) accept = false;  // > 75-¦
                    }
                    if (accept && max_len < 15) accept = false;
                    
                    if (accept) {
                        pca_line = PcaLine{vx, vy, barrel_info->pivot.x, barrel_info->pivot.y,
                                           max_len, "barrel_hough"};
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
        
        // === Fallback: Full mask skeleton + Hough ===
        if (!pca_line) {
            std::vector<std::vector<cv::Point>> contours_skel;
            cv::findContours(motion_mask.clone(), contours_skel, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
            
            if (!contours_skel.empty()) {
                cv::Mat skel;
                zhangSuenThinning(motion_mask, skel);
                
                std::vector<cv::Vec4i> hough_lines;
                cv::HoughLinesP(skel, hough_lines, 1, CV_PI / 180, 12, 15, 8);
                
                if (!hough_lines.empty()) {
                    // Find tip region for proximity scoring
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
                    
                    // Average top-N Hough lines (up to 3) within 30 deg of best
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
                
                // fitLine fallback
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
                
                // Full PCA fallback
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
        
        // Label connected components in pre-chain mask
        cv::Mat walk_mask = (!pre_chain_mask.empty()) ? pre_chain_mask : motion_mask;
        cv::Mat labeled;
        int n_labels = cv::connectedComponents(walk_mask, labeled, 8, CV_32S);
        
        // Walk direction: toward highest Y (board surface)
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
            
            // Search perpendicular corridor if on background
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
                
                // Find blob pixels
                std::vector<cv::Point> blob_pts;
                for (int r = 0; r < h; ++r)
                    for (int c = 0; c < w; ++c)
                        if (labeled.at<int>(r, c) == label)
                            blob_pts.push_back(cv::Point(c, r));
                
                // Blob centroid
                double bx = 0, by = 0;
                for (const auto& p : blob_pts) { bx += p.x; by += p.y; }
                bx /= blob_pts.size(); by /= blob_pts.size();
                
                // Tip candidate = highest Y
                int max_y_idx = 0;
                for (size_t i = 1; i < blob_pts.size(); ++i)
                    if (blob_pts[i].y > blob_pts[max_y_idx].y) max_y_idx = (int)i;
                last_blob_tip = Point2f(blob_pts[max_y_idx].x, blob_pts[max_y_idx].y);
                
                // Re-center on centroid
                current_x = bx; current_y = by;
                
                // Skip past blob
                double max_along = 0;
                for (const auto& p : blob_pts) {
                    double along = (p.x - current_x) * walk_vx + (p.y - current_y) * walk_vy;
                    max_along = std::max(max_along, along);
                }
                step = (int)max_along + 1;
                continue;
            } else if (label == 0 && last_blob_label >= 0) {
                // Gap handling
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
            // Fallback: line walk
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
    *tip = refine_tip_subpixel(*tip, gray, motion_mask);
    
    // Compute line and quality
    double view_quality = 0.3;
    if (flight_centroid) {
        double dx = tip->x - flight_centroid->x;
        double dy = tip->y - flight_centroid->y;
        dart_length = std::sqrt(dx*dx + dy*dy);
        view_quality = std::min(1.0, dart_length / 150.0);
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

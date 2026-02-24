/**
 * pca_detection.cpp - PCA-based barrel line detection (dual pipeline)
 * 
 * Simpler alternative to skeleton/Hough/RANSAC:
 * abs_diff → 26% Otsu threshold → morph → largest contour → PCA axis
 * 
 * Returns a PcaLine that can be warped through existing TPS for triangulation.
 */
#include "dart_detect_internal.h"
#include <iostream>

// ============================================================================
// PCA Barrel Detection
// ============================================================================

std::optional<PcaLine> detect_barrel_pca(
    const cv::Mat& current,
    const cv::Mat& previous,
    double otsu_fraction,
    int morph_kernel_size,
    double min_elongation,
    int min_contour_area)
{
    try {
        // 1. Compute absolute difference
        cv::Mat gray_cur, gray_prev;
        if (current.channels() == 3)
            cv::cvtColor(current, gray_cur, cv::COLOR_BGR2GRAY);
        else
            gray_cur = current;
        if (previous.channels() == 3)
            cv::cvtColor(previous, gray_prev, cv::COLOR_BGR2GRAY);
        else
            gray_prev = previous;
        
        cv::Mat diff;
        cv::absdiff(gray_cur, gray_prev, diff);
        
        // 2. Normalize to 0-255
        double min_val, max_val;
        cv::minMaxLoc(diff, &min_val, &max_val);
        if (max_val < 1.0) return std::nullopt;
        
        cv::Mat norm;
        diff.convertTo(norm, CV_8U, 255.0 / max_val);
        
        // 3. Otsu threshold, then use fraction of it
        cv::Mat otsu_dummy;
        double otsu_val = cv::threshold(norm, otsu_dummy, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
        int thresh = static_cast<int>(otsu_val * otsu_fraction);
        if (thresh < 5) thresh = 5;
        
        cv::Mat mask;
        cv::threshold(norm, mask, thresh, 255, cv::THRESH_BINARY);
        
        // 4. Morphological cleanup: close then open
        cv::Mat kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE,
            cv::Size(morph_kernel_size, morph_kernel_size));
        cv::morphologyEx(mask, mask, cv::MORPH_CLOSE, kernel, cv::Point(-1,-1), 2);
        cv::morphologyEx(mask, mask, cv::MORPH_OPEN, kernel, cv::Point(-1,-1), 1);
        
        // 5. Find largest contour
        std::vector<std::vector<cv::Point>> contours;
        cv::findContours(mask, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_NONE);
        
        if (contours.empty()) return std::nullopt;
        
        int best_idx = 0;
        double best_area = cv::contourArea(contours[0]);
        for (int i = 1; i < (int)contours.size(); ++i) {
            double a = cv::contourArea(contours[i]);
            if (a > best_area) {
                best_area = a;
                best_idx = i;
            }
        }
        
        if (best_area < min_contour_area) return std::nullopt;
        
        // 6. PCA on contour points (CV_32F required)
        const auto& contour = contours[best_idx];
        cv::Mat pts(static_cast<int>(contour.size()), 2, CV_32F);
        for (int i = 0; i < static_cast<int>(contour.size()); ++i) {
            pts.at<float>(i, 0) = static_cast<float>(contour[i].x);
            pts.at<float>(i, 1) = static_cast<float>(contour[i].y);
        }
        
        cv::PCA pca_obj(pts, cv::Mat(), cv::PCA::DATA_AS_ROW, 2);
        
        double cx = pca_obj.mean.at<float>(0, 0);
        double cy = pca_obj.mean.at<float>(0, 1);
        double vx = pca_obj.eigenvectors.at<float>(0, 0);
        double vy = pca_obj.eigenvectors.at<float>(0, 1);
        
        // Elongation from eigenvalues
        double ev0 = pca_obj.eigenvalues.at<float>(0, 0);
        double ev1 = pca_obj.eigenvalues.at<float>(1, 0);
        double elongation = (ev1 > 1e-6) ? (ev0 / ev1) : ev0;
        
        if (elongation < min_elongation) return std::nullopt;
        
        // Ensure consistent direction (vy > 0 = toward bottom of image)
        if (vy < 0) { vx = -vx; vy = -vy; }
        
        PcaLine line;
        line.vx = vx;
        line.vy = vy;
        line.x0 = cx;
        line.y0 = cy;
        line.elongation = elongation;
        line.method = "pca_otsu26";
        
        return line;
    } catch (const cv::Exception& e) {
        std::cerr << "[PCA] OpenCV error: " << e.what() << std::endl;
        return std::nullopt;
    } catch (const std::exception& e) {
        std::cerr << "[PCA] Error: " << e.what() << std::endl;
        return std::nullopt;
    }
}

// ============================================================================
// PCA Triangulation (reuses existing TPS + intersection)
// ============================================================================

std::optional<IntersectionResult> triangulate_pca(
    const std::map<std::string, std::optional<PcaLine>>& pca_lines,
    const std::map<std::string, CameraCalibration>& calibrations)
{
    try {
        // Build warped lines using precomputed TPS
        struct PcaCamLine {
            Point2f p1_norm, p2_norm;
            double elongation;
        };
        
        std::map<std::string, PcaCamLine> cam_lines;
        
        for (const auto& [cam_id, pca_opt] : pca_lines) {
            if (!pca_opt) continue;
            const auto& pca = *pca_opt;
            
            auto cal_it = calibrations.find(cam_id);
            if (cal_it == calibrations.end()) continue;
            
            const auto& tps = cal_it->second.tps_cache;
            if (!tps.valid) continue;
            
            // Sample 21 points along PCA line, warp each through TPS, then fitLine
            double x0 = pca.x0, y0 = pca.y0;
            double vx = pca.vx, vy = pca.vy;
            double extent = 80.0;
            
            std::vector<cv::Point2f> warped_pts;
            for (int t = 0; t < 21; ++t) {
                double frac = (double)t / 20.0;
                double px = x0 + vx * extent * (frac * 2.0 - 1.0);
                double py = y0 + vy * extent * (frac * 2.0 - 1.0);
                Point2f wp = warp_point(tps, px, py);
                warped_pts.push_back(cv::Point2f(wp.x, wp.y));
            }
            
            cv::Vec4f warped_fit;
            cv::fitLine(warped_pts, warped_fit, cv::DIST_HUBER, 0, 0.01, 0.01);
            double wvx = warped_fit[0], wvy = warped_fit[1];
            double wcx = warped_fit[2], wcy = warped_fit[3];
            
            // Extend warped line as ray
            Point2f p1_n(wcx - wvx * 2.0, wcy - wvy * 2.0);
            Point2f p2_n(wcx + wvx * 2.0, wcy + wvy * 2.0);
            
            cam_lines[cam_id] = PcaCamLine{p1_n, p2_n, pca.elongation};
            std::cerr << "[PCA_TRI] " << cam_id << " p1=(" << p1_n.x << "," << p1_n.y 
                      << ") p2=(" << p2_n.x << "," << p2_n.y << ") elong=" << pca.elongation << std::endl;
        }
        
        if (cam_lines.size() < 2) return std::nullopt;
        
        // Pairwise intersections
        std::vector<std::string> cam_ids;
        for (const auto& [id, _] : cam_lines) cam_ids.push_back(id);
        std::sort(cam_ids.begin(), cam_ids.end());
        
        struct PcaIntersection {
            std::string cam1, cam2;
            Point2f coords;
            double crossing_angle;
            double combined_elongation;
        };
        
        std::vector<PcaIntersection> intersections;
        
        for (size_t i = 0; i < cam_ids.size(); ++i) {
            for (size_t j = i + 1; j < cam_ids.size(); ++j) {
                const auto& l1 = cam_lines[cam_ids[i]];
                const auto& l2 = cam_lines[cam_ids[j]];
                
                // Local intersection without radius limit
                double x1 = l1.p1_norm.x, y1 = l1.p1_norm.y;
                double x2 = l1.p2_norm.x, y2 = l1.p2_norm.y;
                double x3 = l2.p1_norm.x, y3 = l2.p1_norm.y;
                double x4 = l2.p2_norm.x, y4 = l2.p2_norm.y;
                double denom = (x1-x2)*(y3-y4) - (y1-y2)*(x3-x4);
                double len1 = std::hypot(x2-x1, y2-y1);
                double len2 = std::hypot(x4-x3, y4-y3);
                if (len1 < 1e-12 || len2 < 1e-12) continue;
                double sin_angle = std::abs(denom) / (len1 * len2);
                if (sin_angle < 0.17) continue;  // ~10 degrees
                double t = ((x1-x3)*(y3-y4) - (y1-y3)*(x3-x4)) / denom;
                double ixx = x1 + t*(x2-x1);
                double iyy = y1 + t*(y2-y1);
                std::optional<Point2f> ix = Point2f(ixx, iyy);
                
                if (!ix) { std::cerr << "[PCA_TRI] " << cam_ids[i] << "+" << cam_ids[j] << " no intersection" << std::endl; continue; }
                
                double dist = std::sqrt(ix->x * ix->x + ix->y * ix->y);
                std::cerr << "[PCA_TRI] " << cam_ids[i] << "+" << cam_ids[j] << " ix=(" << ix->x << "," << ix->y << ") dist=" << dist << std::endl;
                if (dist > 1.5) continue;
                
                // Crossing angle
                double dx1 = l1.p2_norm.x - l1.p1_norm.x;
                double dy1 = l1.p2_norm.y - l1.p1_norm.y;
                double dx2 = l2.p2_norm.x - l2.p1_norm.x;
                double dy2 = l2.p2_norm.y - l2.p1_norm.y;
                double dot = std::abs(dx1*dx2 + dy1*dy2) / 
                    (std::sqrt(dx1*dx1+dy1*dy1) * std::sqrt(dx2*dx2+dy2*dy2) + 1e-10);
                double cross_angle = std::acos(std::min(dot, 1.0)) * 180.0 / CV_PI;
                if (cross_angle > 90) cross_angle = 180 - cross_angle;
                
                if (cross_angle < 10) continue;
                
                intersections.push_back(PcaIntersection{
                    cam_ids[i], cam_ids[j], *ix, cross_angle,
                    l1.elongation + l2.elongation
                });
            }
        }
        
        if (intersections.empty()) return std::nullopt;
        
        // Pick best intersection: highest combined elongation
        const PcaIntersection* best = &intersections[0];
        for (const auto& ix : intersections) {
            if (ix.combined_elongation > best->combined_elongation)
                best = &ix;
        }
        
        // Score in normalized board space
        double ix_dist = std::sqrt(best->coords.x * best->coords.x + best->coords.y * best->coords.y);
        double ix_angle_cw_rad = std::atan2(best->coords.x, best->coords.y);
        double ix_angle_cw_deg = ix_angle_cw_rad * 180.0 / CV_PI;
        
        // Segment: wire at board_idx*18-9, so segment center at board_idx*18
        double seg_deg = std::fmod(ix_angle_cw_deg + 9.0, 360.0);
        if (seg_deg < 0) seg_deg += 360.0;
        int seg_idx = static_cast<int>(seg_deg / 18.0) % 20;
        int segment = SEGMENT_ORDER[seg_idx];
        
        // Ring/multiplier from distance
        int multiplier = 1;
        if (ix_dist < BULLSEYE_NORM) {
            segment = 25; multiplier = 2;
        } else if (ix_dist < OUTER_BULL_NORM) {
            segment = 25; multiplier = 1;
        } else if (ix_dist < TRIPLE_INNER_NORM) {
            multiplier = 1;
        } else if (ix_dist < TRIPLE_OUTER_NORM) {
            multiplier = 3;
        } else if (ix_dist < DOUBLE_INNER_NORM) {
            multiplier = 1;
        } else if (ix_dist <= DOUBLE_OUTER_NORM * 1.03) {
            multiplier = 2;
        } else {
            segment = 0; multiplier = 0;
        }
        
        IntersectionResult result;
        result.segment = segment;
        result.multiplier = multiplier;
        result.score = segment * multiplier;
        result.method = "PCA_dual";
        result.confidence = 0.7;
        result.coords = best->coords;
        result.total_error = 0;
        
        return result;
    } catch (const std::exception& e) {
        std::cerr << "[PCA_TRI] Error: " << e.what() << std::endl;
        return std::nullopt;
    }
}

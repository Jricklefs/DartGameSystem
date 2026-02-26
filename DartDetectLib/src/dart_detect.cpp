/**
 * dart_detect.cpp - Main entry points (C API for P/Invoke)
 * 
 * Orchestrates the full detection pipeline:
 * decode images -> detect per camera -> triangulate -> vote -> return JSON
 */
#include "dart_detect.h"
#include "dart_detect_internal.h"

#include <opencv2/imgcodecs.hpp>
#include <string>
#include <map>
#include <mutex>
#include <future>
#include <sstream>
#include <cstring>
#include <iostream>

// Simple JSON builder (avoids external dependency)
#include <vector>

// ============================================================================
// Global State
// ============================================================================

static std::map<std::string, CameraCalibration> g_calibrations;
static bool g_pca_enabled = false;
static std::map<std::string, BoardCache> g_board_caches;
static std::mutex g_mutex;
static bool g_initialized = false;

// ============================================================================
// Simple JSON helpers
// ============================================================================

static std::string json_string(const std::string& key, const std::string& val) {
    return "\"" + key + "\":\"" + val + "\"";
}

static std::string json_int(const std::string& key, int val) {
    return "\"" + key + "\":" + std::to_string(val);
}

static std::string json_double(const std::string& key, double val) {
    std::ostringstream oss;
    oss << "\"" << key << "\":" << val;
    return oss.str();
}

// ============================================================================
// JSON Parsing (minimal, handles our calibration format)
// ============================================================================

static std::string extract_json_value(const std::string& json, const std::string& key) {
    std::string search = "\"" + key + "\"";
    size_t pos = json.find(search);
    if (pos == std::string::npos) return "";
    
    pos = json.find(':', pos + search.length());
    if (pos == std::string::npos) return "";
    ++pos;
    
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\n' || json[pos] == '\r'))
        ++pos;
    
    if (pos >= json.length()) return "";
    
    char first = json[pos];
    if (first == '"') {
        size_t end = json.find('"', pos + 1);
        if (end == std::string::npos) return "";
        return json.substr(pos + 1, end - pos - 1);
    }
    if (first == '[' || first == '{') {
        char open = first, close = (first == '[') ? ']' : '}';
        int depth = 1;
        size_t i = pos + 1;
        while (i < json.length() && depth > 0) {
            if (json[i] == open) ++depth;
            else if (json[i] == close) --depth;
            ++i;
        }
        return json.substr(pos, i - pos);
    }
    size_t end = json.find_first_of(",}] \t\n\r", pos);
    if (end == std::string::npos) end = json.length();
    return json.substr(pos, end - pos);
}

static std::vector<double> parse_double_array(const std::string& arr) {
    std::vector<double> result;
    size_t pos = 0;
    while (pos < arr.length()) {
        while (pos < arr.length() && arr[pos] != '-' && arr[pos] != '.' && !std::isdigit(arr[pos]))
            ++pos;
        if (pos >= arr.length()) break;
        
        size_t end = pos;
        while (end < arr.length() && (arr[end] == '-' || arr[end] == '.' || std::isdigit(arr[end]) || arr[end] == 'e' || arr[end] == 'E' || arr[end] == '+'))
            ++end;
        
        if (end > pos) {
            try {
                result.push_back(std::stod(arr.substr(pos, end - pos)));
            } catch (...) {}
        }
        pos = end;
    }
    return result;
}

static std::optional<EllipseData> parse_ellipse(const std::string& json, const std::string& key) {
    std::string val = extract_json_value(json, key);
    if (val.empty() || val == "null") return std::nullopt;
    
    auto nums = parse_double_array(val);
    if (nums.size() < 5) return std::nullopt;
    
    EllipseData e;
    e.cx = nums[0]; e.cy = nums[1];
    e.width = nums[2]; e.height = nums[3];
    e.rotation_deg = nums[4];
    return e;
}

static bool parse_camera_calibration(const std::string& json, CameraCalibration& cal) {
    std::string center_str = extract_json_value(json, "center");
    auto center_nums = parse_double_array(center_str);
    if (center_nums.size() < 2) return false;
    cal.center = Point2f(center_nums[0], center_nums[1]);
    
    std::string angles_str = extract_json_value(json, "segment_angles");
    cal.segment_angles = parse_double_array(angles_str);
    if (cal.segment_angles.size() < 20) return false;
    
    std::string s20 = extract_json_value(json, "segment_20_index");
    if (!s20.empty()) cal.segment_20_index = std::stoi(s20);
    
    cal.outer_double_ellipse = parse_ellipse(json, "outer_double_ellipse");
    cal.inner_double_ellipse = parse_ellipse(json, "inner_double_ellipse");
    cal.outer_triple_ellipse = parse_ellipse(json, "outer_triple_ellipse");
    cal.inner_triple_ellipse = parse_ellipse(json, "inner_triple_ellipse");
    cal.bull_ellipse = parse_ellipse(json, "bull_ellipse");
    cal.bullseye_ellipse = parse_ellipse(json, "bullseye_ellipse");
    
    return true;
}

bool parse_calibrations(const std::string& json,
                        std::map<std::string, CameraCalibration>& out)
{
    for (const char* cam : {"cam0", "cam1", "cam2"}) {
        std::string cam_json = extract_json_value(json, cam);
        if (cam_json.empty()) continue;
        
        CameraCalibration cal;
        if (parse_camera_calibration(cam_json, cal)) {
            out[cam] = cal;
        }
    }
    return !out.empty();
}

// ============================================================================
// Phase 3: Compute board ROI from outer double ellipse
// ============================================================================
#ifdef ENABLE_ROI_CROP
static cv::Rect compute_board_roi(const CameraCalibration& cal, int img_width, int img_height) {
    if (!cal.outer_double_ellipse) return cv::Rect(0, 0, img_width, img_height);
    
    const auto& ell = *cal.outer_double_ellipse;
    // Sample ellipse at many angles to find bounding box
    double min_x = 1e9, max_x = -1e9, min_y = 1e9, max_y = -1e9;
    double a = ell.width / 2.0, b = ell.height / 2.0;
    double rot = ell.rotation_deg * CV_PI / 180.0;
    double cos_r = std::cos(rot), sin_r = std::sin(rot);
    
    for (int i = 0; i < 360; ++i) {
        double theta = i * CV_PI / 180.0;
        double x = ell.cx + a * std::cos(theta) * cos_r - b * std::sin(theta) * sin_r;
        double y = ell.cy + a * std::cos(theta) * sin_r + b * std::sin(theta) * cos_r;
        min_x = std::min(min_x, x); max_x = std::max(max_x, x);
        min_y = std::min(min_y, y); max_y = std::max(max_y, y);
    }
    
    // Add 50px margin
    int margin = 50;
    int x0 = std::max(0, (int)std::floor(min_x) - margin);
    int y0 = std::max(0, (int)std::floor(min_y) - margin);
    int x1 = std::min(img_width, (int)std::ceil(max_x) + margin);
    int y1 = std::min(img_height, (int)std::ceil(max_y) + margin);
    
    return cv::Rect(x0, y0, x1 - x0, y1 - y0);
}
#endif

// ============================================================================
// Image Decoding
// ============================================================================

cv::Mat decode_image(const unsigned char* data, int size)
{
    std::vector<unsigned char> buf(data, data + size);
    return cv::imdecode(buf, cv::IMREAD_COLOR);
}

// ============================================================================
// C API Implementation
// ============================================================================

DD_API int dd_init(const char* calibration_json)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    
    g_calibrations.clear();
    std::string json(calibration_json);
    
    if (!parse_calibrations(json, g_calibrations)) {
        return -1;
    }
    
    // === TPS PRECOMPUTATION (Feb 19, 2026) ===
    for (auto& [cam_id, cal] : g_calibrations) {
        cal.tps_cache = build_tps_transform(cal);
        
        // Phase 3 (Change 3): Validate segment_angles monotonicity
        if (cal.segment_angles.size() >= 20) {
            // Normalize all angles to [0, 2*pi)
            for (auto& a : cal.segment_angles) {
                while (a < 0) a += 2.0 * CV_PI;
                while (a >= 2.0 * CV_PI) a -= 2.0 * CV_PI;
            }
            // Verify monotonically increasing
            bool monotonic = true;
            for (int i = 1; i < (int)cal.segment_angles.size(); ++i) {
                // Allow wraparound at last->first boundary, but consecutive should increase
                if (cal.segment_angles[i] <= cal.segment_angles[i - 1]) {
                    // Check if it's the wraparound point (one allowed)
                    // Count how many decreases there are
                    monotonic = false;
                    break;
                }
            }
            if (!monotonic) {
                // Count actual wraparound points (should be at most 1)
                int wraps = 0;
                for (int i = 1; i < (int)cal.segment_angles.size(); ++i) {
                    if (cal.segment_angles[i] < cal.segment_angles[i - 1]) wraps++;
                }
                if (wraps > 1) {
                    std::cerr << "[DartDetect] WARNING: segment_angles for " << cam_id
                              << " are not monotonically increasing (" << wraps
                              << " wraparound points, expected at most 1)" << std::endl;
                }
            }
        }
    }
    
    g_initialized = true;
    return 0;
}

DD_API const char* dd_detect(
    int dart_number,
    const char* board_id,
    int num_cameras,
    const char** camera_ids,
    const unsigned char** current_images,
    const int* current_sizes,
    const unsigned char** before_images,
    const int* before_sizes)
{
    if (!g_initialized || num_cameras <= 0) {
        char* err = new char[64];
        std::strcpy(err, "{\"error\":\"not initialized\"}");
        return err;
    }
    if (!current_images || !current_sizes || !before_images || !before_sizes) {
        char* err = new char[80];
        std::strcpy(err, "{\"error\":\"missing image buffers\"}");
        return err;
    }
    
    std::string bid(board_id ? board_id : "default");
    auto& cache = g_board_caches[bid];
    
    struct CameraTask {
        std::string cam_id;
        int index;
        CameraCalibration cal;
    };
    std::vector<CameraTask> tasks;
    for (int i = 0; i < num_cameras && i < 3; ++i) {
        std::string cam_id;
        if (camera_ids && camera_ids[i] && camera_ids[i][0] != '\0') {
            cam_id = camera_ids[i];
        } else {
            cam_id = "cam" + std::to_string(i);
        }
        auto cal_it = g_calibrations.find(cam_id);
        if (cal_it == g_calibrations.end()) continue;
        tasks.push_back({cam_id, i, cal_it->second});
    }

    struct CameraResult {
        std::string cam_id;
        DetectionResult det;
        CameraCalibration cal;
    };
    std::vector<std::future<std::optional<CameraResult>>> futures;
    for (const auto& task : tasks) {
        futures.push_back(std::async(std::launch::async,
            [&, task]() -> std::optional<CameraResult> {
                cv::Mat current_raw = decode_image(current_images[task.index], current_sizes[task.index]);
                cv::Mat before_raw = decode_image(before_images[task.index], before_sizes[task.index]);
                if (current_raw.empty() || before_raw.empty()) return std::nullopt;
                
                // Unsharp mask sharpening for better edge detection
                cv::Mat current, before;
                {
                    cv::Mat blur_c, blur_b;
                    cv::GaussianBlur(current_raw, blur_c, cv::Size(0, 0), 3.0);
                    cv::GaussianBlur(before_raw, blur_b, cv::Size(0, 0), 3.0);
                    cv::addWeighted(current_raw, 1.0 + 0.7, blur_c, -0.7, 0, current);
                    cv::addWeighted(before_raw, 1.0 + 0.7, blur_b, -0.7, 0, before);
                }
                // Gamma 0.6 correction
                {
                    cv::Mat lut(1, 256, CV_8U);
                    for (int i = 0; i < 256; i++)
                        lut.at<uchar>(0, i) = cv::saturate_cast<uchar>(255.0 * std::pow(i / 255.0, 0.6));
                    cv::LUT(current, lut, current);
                    cv::LUT(before, lut, before);
                }
                // Desaturate to 0.5
                {
                    cv::Mat hsv_c, hsv_b;
                    cv::cvtColor(current, hsv_c, cv::COLOR_BGR2HSV);
                    cv::cvtColor(before, hsv_b, cv::COLOR_BGR2HSV);
                    std::vector<cv::Mat> ch_c, ch_b;
                    cv::split(hsv_c, ch_c); cv::split(hsv_b, ch_b);
                    ch_c[1] = ch_c[1] * 0.5;
                    ch_b[1] = ch_b[1] * 0.5;
                    cv::merge(ch_c, hsv_c); cv::merge(ch_b, hsv_b);
                    cv::cvtColor(hsv_c, current, cv::COLOR_HSV2BGR);
                    cv::cvtColor(hsv_b, before, cv::COLOR_HSV2BGR);
                }

                // Phase 3 (Change 4): Compute resolution scale from image height
                double res_scale = compute_resolution_scale(current.rows);
                
                // Phase 3 (Change 1): Board ROI cropping
                Point2f detect_center = task.cal.center;
                cv::Rect roi(0, 0, current.cols, current.rows);
#ifdef ENABLE_ROI_CROP
                if (task.cal.outer_double_ellipse) {
                    roi = compute_board_roi(task.cal, current.cols, current.rows);
                    // Crop images to ROI
                    current = current(roi).clone();
                    before = before(roi).clone();
                    // Offset board center into ROI space
                    detect_center = Point2f(task.cal.center.x - roi.x, task.cal.center.y - roi.y);
                }
#endif

                std::vector<cv::Mat> prev_masks;
                if (dart_number > 1) {
                    prev_masks = cache.get_masks(task.cam_id);
#ifdef ENABLE_ROI_CROP
                    // Crop previous masks to same ROI
                    if (task.cal.outer_double_ellipse) {
                        for (auto& pm : prev_masks) {
                            if (!pm.empty() && pm.rows > roi.y + roi.height && pm.cols > roi.x + roi.width) {
                                pm = pm(roi).clone();
                            }
                        }
                    }
#endif
                }

                DetectionResult det = detect_dart(
                    current, before, detect_center, prev_masks, 30, res_scale);

#ifdef ENABLE_ROI_CROP
                // Transform tip back to full-image space
                if (det.tip && task.cal.outer_double_ellipse) {
                    det.tip = Point2f(det.tip->x + roi.x, det.tip->y + roi.y);
                }
                // Transform PCA line origin back to full-image space
                if (det.pca_line && task.cal.outer_double_ellipse) {
                    det.pca_line->x0 += roi.x;
                    det.pca_line->y0 += roi.y;
                }
                // Motion mask needs to be full-size for board cache
                if (!det.motion_mask.empty() && task.cal.outer_double_ellipse) {
                    cv::Mat full_mask = cv::Mat::zeros(roi.y + roi.height + 50, roi.x + roi.width + 50, CV_8U);
                    // Ensure full_mask is large enough
                    int full_h = std::max(full_mask.rows, roi.y + det.motion_mask.rows);
                    int full_w = std::max(full_mask.cols, roi.x + det.motion_mask.cols);
                    full_mask = cv::Mat::zeros(full_h, full_w, CV_8U);
                    det.motion_mask.copyTo(full_mask(cv::Rect(roi.x, roi.y,
                        det.motion_mask.cols, det.motion_mask.rows)));
                    det.motion_mask = full_mask;
                }
#endif

                if (det.tip) {
                    return CameraResult{task.cam_id, det, task.cal};
                }
                return std::nullopt;
            }));
    }

    std::map<std::string, DetectionResult> camera_results;
    std::map<std::string, CameraCalibration> active_cals;
    for (auto& f : futures) {
        auto result = f.get();
        if (result) {
            camera_results[result->cam_id] = result->det;
            active_cals[result->cam_id] = result->cal;
        }
    }
    
    for (const auto& [cam_id, det] : camera_results) {
        if (!det.motion_mask.empty()) {
            cache.add_mask(cam_id, det.motion_mask);
        }
    }
    
    // Build result JSON
    std::ostringstream json;
    json << "{";
    
    if (camera_results.size() >= 2) {
        auto tri = triangulate_with_line_intersection(camera_results, active_cals);
        
        if (tri) {
            json << json_int("segment", tri->segment) << ","
                 << json_int("multiplier", tri->multiplier) << ","
                 << json_int("score", tri->score) << ","
                 << json_string("method", tri->method) << ","
                 << json_double("confidence", tri->confidence) << ","
                 << json_double("total_error", tri->total_error)
                 << ",\"coords_x\":" << tri->coords.x
                 << ",\"coords_y\":" << tri->coords.y;
            
            json << ",\"per_camera\":{";
            bool first = true;
            for (const auto& [cam_id, vote] : tri->per_camera) {
                if (!first) json << ",";
                json << "\"" << cam_id << "\":{"
                     << json_int("segment", vote.segment) << ","
                     << json_int("multiplier", vote.multiplier) << ","
                     << json_int("score", vote.score) << ","
                     << json_string("zone", vote.zone)
                     << "}";
                first = false;
            }
            json << "}";
            
            // Camera detection details (barrel method, tip method, quality)
            json << ",\"camera_details\":{";
            bool first_det = true;
            for (const auto& [cam_id, det] : camera_results) {
                if (!first_det) json << ",";
                std::string bm = "none";
                if (det.pca_line) bm = det.pca_line->method;
                json << "\"" << cam_id << "\":{"
                     << json_string("tip_method", det.method) << ","
                     << json_string("barrel_method", bm) << ","
                     << json_double("mask_quality", det.mask_quality) << ","
                     << json_double("ransac_inlier_ratio", det.ransac_inlier_ratio) << ","
                     << json_double("barrel_aspect", det.barrel_aspect_ratio) << ","
                     << json_double("tip_x", det.tip ? det.tip->x : -1) << ","
                     << json_double("tip_y", det.tip ? det.tip->y : -1) << ","
                     << json_double("line_vx", det.pca_line ? det.pca_line->vx : -999) << ","
                     << json_double("line_vy", det.pca_line ? det.pca_line->vy : 0) << ","
                     << json_double("line_x0", det.pca_line ? det.pca_line->x0 : 0) << ","
                     << json_double("line_y0", det.pca_line ? det.pca_line->y0 : 0) << ","
                     << json_double("line_elongation", det.pca_line ? det.pca_line->elongation : 0)
                     << "}";
                first_det = false;
            }
            json << "}";

            // Phase 7: Triangulation debug export
            if (tri->tri_debug) {
                const auto& td = *tri->tri_debug;
                json << ",\"tri_debug\":{"
                     << json_double("angle_spread_deg", td.angle_spread_deg) << ","
                     << json_double("median_residual", td.median_residual) << ","
                     << json_double("max_residual", td.max_residual) << ","
                     << json_double("residual_spread", td.residual_spread) << ","
                     << json_double("final_confidence", td.final_confidence) << ","
                     << "\"camera_dropped\":" << (td.camera_dropped ? "true" : "false") << ","
                     << json_string("dropped_cam_id", td.dropped_cam_id) << ","
                     << json_double("board_radius", td.board_radius) << ","
                     << json_string("radius_gate_reason", td.radius_gate_reason) << ","
                     << ",\"segment_label_corrected\":" << (td.segment_label_corrected ? "true" : "false");
                json << ",\"cam_debug\":{";
                bool first_cd = true;
                for (const auto& [cid, cd] : td.cam_debug) {
                    if (!first_cd) json << ",";
                    json << "\"" << cid << "\":{"
                         << json_double("warped_dir_x", cd.warped_dir_x) << ","
                         << json_double("warped_dir_y", cd.warped_dir_y) << ","
                         << json_double("perp_residual", cd.perp_residual) << ","
                         << json_int("barrel_pixel_count", cd.barrel_pixel_count) << ","
                         << json_double("barrel_aspect_ratio", cd.barrel_aspect_ratio) << ","
                         << json_double("detection_quality", cd.detection_quality) << ","
                         << ",\"weak_barrel_signal\":" << (cd.weak_barrel_signal ? "true" : "false") << ","
                         << json_double("warped_point_x", cd.warped_point_x) << ","
                         << json_double("warped_point_y", cd.warped_point_y)
                         << "}";
                    first_cd = false;
                }
                json << "}}";
            }

            // === PCA DUAL PIPELINE ===
            // Run PCA-based barrel detection (only when enabled)
            if (g_pca_enabled) { // PCA toggle gate
            // Uses: 26% Otsu threshold -> PCA on largest contour -> TPS warp -> intersect
            try {
                std::map<std::string, std::optional<PcaLine>> pca_lines;
                for (const auto& [cam_id, det] : camera_results) {
                    auto cal_it = active_cals.find(cam_id);
                    if (cal_it == active_cals.end()) continue;

                    // Find camera index for image access
                    int cam_idx = -1;
                    for (const auto& t : tasks) {
                        if (t.cam_id == cam_id) { cam_idx = t.index; break; }
                    }
                    if (cam_idx < 0) continue;

                    cv::Mat cur_raw = decode_image(current_images[cam_idx], current_sizes[cam_idx]);
                    cv::Mat bef_raw = decode_image(before_images[cam_idx], before_sizes[cam_idx]);
                    if (cur_raw.empty() || bef_raw.empty()) continue;

                    // Same enhancement: USM 0.7 + gamma 0.6 + desat 0.5
                    cv::Mat cur_enh, bef_enh;
                    cv::Mat bc, bb;
                    cv::GaussianBlur(cur_raw, bc, cv::Size(0,0), 3.0);
                    cv::GaussianBlur(bef_raw, bb, cv::Size(0,0), 3.0);
                    cv::addWeighted(cur_raw, 1.7, bc, -0.7, 0, cur_enh);
                    cv::addWeighted(bef_raw, 1.7, bb, -0.7, 0, bef_enh);

                    cv::Mat lut(1, 256, CV_8U);
                    for (int gi = 0; gi < 256; gi++)
                        lut.at<uchar>(0, gi) = cv::saturate_cast<uchar>(255.0 * std::pow(gi / 255.0, 0.6));
                    cv::LUT(cur_enh, lut, cur_enh);
                    cv::LUT(bef_enh, lut, bef_enh);

                    cv::Mat hc, hb;
                    cv::cvtColor(cur_enh, hc, cv::COLOR_BGR2HSV);
                    cv::cvtColor(bef_enh, hb, cv::COLOR_BGR2HSV);
                    std::vector<cv::Mat> cc, cb;
                    cv::split(hc, cc); cv::split(hb, cb);
                    cc[1] = cc[1] * 0.5; cb[1] = cb[1] * 0.5;
                    cv::merge(cc, hc); cv::merge(cb, hb);
                    cv::cvtColor(hc, cur_enh, cv::COLOR_HSV2BGR);
                    cv::cvtColor(hb, bef_enh, cv::COLOR_HSV2BGR);

                    auto pca = detect_barrel_pca(cur_enh, bef_enh);
                    pca_lines[cam_id] = pca;
                }

                auto pca_tri = triangulate_pca(pca_lines, active_cals);

                json << ",\"pca_result\":{";
                if (pca_tri) {
                    json << json_int("segment", pca_tri->segment) << ","
                         << json_int("multiplier", pca_tri->multiplier) << ","
                         << json_int("score", pca_tri->score) << ","
                         << json_string("method", pca_tri->method) << ","
                         << json_double("confidence", pca_tri->confidence);
                    json << ",\"cameras\":{";
                    bool pf = true;
                    for (const auto& [cid, pl] : pca_lines) {
                        if (!pf) json << ",";
                        json << "\"" << cid << "\":{";
                        if (pl) {
                            json << json_double("elongation", pl->elongation) << ","
                                 << json_string("method", pl->method);
                        } else {
                            json << "\"elongation\":0";
                        }
                        json << "}";
                        pf = false;
                    }
                    json << "}";
                } else {
                    json << json_string("method", "no_pca") << ","
                         << json_int("segment", 0) << ","
                         << json_int("multiplier", 0);
                }
                json << "}";
            } catch (const std::exception& ex) {
                json << ",\"pca_result\":{\"method\":\"error\",\"segment\":0,\"multiplier\":0}";
            } catch (...) {
                json << ",\"pca_result\":{\"method\":\"crash\",\"segment\":0,\"multiplier\":0}";
            } // end PCA toggle gate
            }
        } else {
            goto single_camera;
        }
    } else if (camera_results.size() == 1) {
        single_camera:
        auto& [cam_id, det] = *camera_results.begin();
        auto cal_it = active_cals.find(cam_id);
        if (cal_it != active_cals.end() && det.tip) {
            ScoreResult score = score_from_ellipse_calibration(
                det.tip->x, det.tip->y, cal_it->second);
            
            json << json_int("segment", score.segment) << ","
                 << json_int("multiplier", score.multiplier) << ","
                 << json_int("score", score.score) << ","
                 << json_string("method", "SingleCam_" + det.method) << ","
                 << json_double("confidence", det.confidence * 0.5);
        } else {
            json << json_int("segment", 0) << ","
                 << json_int("multiplier", 0) << ","
                 << json_int("score", 0) << ","
                 << json_string("method", "none") << ","
                 << json_double("confidence", 0.0);
        }
    } else {
        json << json_int("segment", 0) << ","
             << json_int("multiplier", 0) << ","
             << json_int("score", 0) << ","
             << json_string("method", "no_detection") << ","
             << json_double("confidence", 0.0);
    }
    
    json << "}";
    
    std::string result = json.str();
    char* out = new char[result.length() + 1];
    std::strcpy(out, result.c_str());
    return out;
}

DD_API void dd_init_board(const char* board_id)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    std::string bid(board_id ? board_id : "default");
    g_board_caches[bid].clear();
}

DD_API void dd_clear_board(const char* board_id)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    std::string bid(board_id ? board_id : "default");
    g_board_caches.erase(bid);
}

DD_API void dd_free_string(const char* str)
{
    delete[] str;
}

DD_API const char* dd_version(void)
{
    return "DartDetectLib 1.0.0 (ported from Python v10.2)";
}


// ============================================================================
// Fronton (Top-Down) View
// ============================================================================

// Build inverse TPS: from normalized board coords back to pixel coords
static TpsTransform build_inverse_tps(const CameraCalibration& cal)
{
    // The forward TPS goes pixel -> normalized.
    // We need normalized -> pixel, so swap src/dst.
    TpsTransform inv;
    inv.valid = false;

    const auto& fwd = cal.tps_cache;
    if (!fwd.valid) return inv;

    int N = fwd.src_points.rows;
    if (N < 4) return inv;

    // For inverse: src = normalized board coords, dst = pixel coords
    std::vector<double> src_x(N), src_y(N), dst_x(N), dst_y(N);
    for (int i = 0; i < N; ++i) {
        src_x[i] = fwd.dst_points.at<double>(i, 0);  // normalized
        src_y[i] = fwd.dst_points.at<double>(i, 1);
        dst_x[i] = fwd.src_points.at<double>(i, 0);  // pixel
        dst_y[i] = fwd.src_points.at<double>(i, 1);
    }

    // Build TPS system (same math as build_tps_transform but with swapped points)
    auto tps_basis_d = [](double x1, double y1, double x2, double y2) -> double {
        double r = std::sqrt((x1-x2)*(x1-x2) + (y1-y2)*(y1-y2));
        if (r < 1e-10) return 0.0;
        return r * r * std::log(r);
    };

    cv::Mat K(N, N, CV_64F);
    for (int i = 0; i < N; ++i)
        for (int j = 0; j < N; ++j)
            K.at<double>(i, j) = tps_basis_d(src_x[i], src_y[i], src_x[j], src_y[j]);

    cv::Mat P(N, 3, CV_64F);
    for (int i = 0; i < N; ++i) {
        P.at<double>(i, 0) = 1.0;
        P.at<double>(i, 1) = src_x[i];
        P.at<double>(i, 2) = src_y[i];
    }

    int M = N + 3;
    cv::Mat L = cv::Mat::zeros(M, M, CV_64F);
    K.copyTo(L(cv::Range(0, N), cv::Range(0, N)));
    P.copyTo(L(cv::Range(0, N), cv::Range(N, M)));
    cv::Mat(P.t()).copyTo(L(cv::Range(N, M), cv::Range(0, N)));

    cv::Mat rhs_x = cv::Mat::zeros(M, 1, CV_64F);
    cv::Mat rhs_y = cv::Mat::zeros(M, 1, CV_64F);
    for (int i = 0; i < N; ++i) {
        rhs_x.at<double>(i) = dst_x[i];
        rhs_y.at<double>(i) = dst_y[i];
    }

    cv::Mat sol_x, sol_y;
    if (!cv::solve(L, rhs_x, sol_x, cv::DECOMP_SVD)) return inv;
    if (!cv::solve(L, rhs_y, sol_y, cv::DECOMP_SVD)) return inv;

    inv.src_points = cv::Mat(N, 2, CV_64F);
    inv.dst_points = cv::Mat(N, 2, CV_64F);
    for (int i = 0; i < N; ++i) {
        inv.src_points.at<double>(i, 0) = src_x[i];
        inv.src_points.at<double>(i, 1) = src_y[i];
        inv.dst_points.at<double>(i, 0) = dst_x[i];
        inv.dst_points.at<double>(i, 1) = dst_y[i];
    }

    inv.weights = cv::Mat(M, 2, CV_64F);
    for (int i = 0; i < M; ++i) {
        inv.weights.at<double>(i, 0) = sol_x.at<double>(i);
        inv.weights.at<double>(i, 1) = sol_y.at<double>(i);
    }

    inv.valid = true;
    return inv;
}

// Draw the spider (wire overlay) on a fronton image
static void draw_spider_overlay(cv::Mat& img, int size)
{
    double cx = size / 2.0;
    double cy = size / 2.0;
    double scale = size / 2.0;  // 1.0 normalized = edge of image

    cv::Scalar wire_color(0, 255, 255);  // Yellow
    int thickness = 1;

    // Ring radii (normalized, same as in build_tps_transform)
    double rings[] = {
        6.35 / 170.0,   // bullseye
        16.0 / 170.0,   // bull
        99.0 / 170.0,   // triple inner
        107.0 / 170.0,  // triple outer
        162.0 / 170.0,  // double inner
        170.0 / 170.0   // double outer
    };

    for (double r : rings) {
        int radius_px = (int)(r * scale);
        cv::circle(img, cv::Point((int)cx, (int)cy), radius_px, wire_color, thickness);
    }

    // Segment lines: 20 lines at 18-degree intervals
    // Segment 20 is at top (12 o'clock), boundaries at -9 and +9 degrees
    double bull_r = 16.0 / 170.0 * scale;
    double outer_r = 170.0 / 170.0 * scale;
    for (int i = 0; i < 20; ++i) {
        double angle_deg = i * 18.0 - 9.0;  // boundary angle in CW degrees from 12 o'clock
        double angle_rad = angle_deg * CV_PI / 180.0;
        // In our coordinate system: x = sin(angle), y = -cos(angle) for CW from top
        // But fronton maps: board_x -> img_x, board_y -> img_y (with y inverted to put 20 at top)
        double dx = std::sin(angle_rad);
        double dy = -std::cos(angle_rad);
        int x1 = (int)(cx + bull_r * dx);
        int y1 = (int)(cy + bull_r * dy);
        int x2 = (int)(cx + outer_r * dx);
        int y2 = (int)(cy + outer_r * dy);
        cv::line(img, cv::Point(x1, y1), cv::Point(x2, y2), wire_color, thickness);
    }

    // Segment number labels
    static const int SEG_ORDER[20] = {20,1,18,4,13,6,10,15,2,17,3,19,7,16,8,11,14,9,12,5};
    double label_r = 185.0 / 170.0 * scale;  // Just outside the board
    for (int i = 0; i < 20; ++i) {
        double angle_deg = i * 18.0;  // center of segment
        double angle_rad = angle_deg * CV_PI / 180.0;
        double dx = std::sin(angle_rad);
        double dy = -std::cos(angle_rad);
        int lx = (int)(cx + label_r * dx);
        int ly = (int)(cy + label_r * dy);

        std::string label = std::to_string(SEG_ORDER[i]);
        int baseline = 0;
        cv::Size ts = cv::getTextSize(label, cv::FONT_HERSHEY_SIMPLEX, 0.4, 1, &baseline);
        cv::putText(img, label, cv::Point(lx - ts.width/2, ly + ts.height/2),
                    cv::FONT_HERSHEY_SIMPLEX, 0.4, cv::Scalar(255, 255, 255), 1);
    }
}

DD_API int GetFrontonView(
    int camera_index,
    const unsigned char* input_jpeg, int input_len,
    unsigned char* output_jpeg, int* output_len,
    int output_size)
{
    std::lock_guard<std::mutex> lock(g_mutex);

    if (!g_initialized || !input_jpeg || !output_jpeg || !output_len) return -1;

    // Find calibration for this camera
    std::string cam_id = "cam" + std::to_string(camera_index);
    auto cal_it = g_calibrations.find(cam_id);
    if (cal_it == g_calibrations.end()) return -1;
    const auto& cal = cal_it->second;
    if (!cal.tps_cache.valid) return -1;

    // Decode input JPEG
    std::vector<unsigned char> buf(input_jpeg, input_jpeg + input_len);
    cv::Mat input_img = cv::imdecode(buf, cv::IMREAD_COLOR);
    if (input_img.empty()) return -1;

    // Build inverse TPS (normalized -> pixel)
    TpsTransform inv_tps = build_inverse_tps(cal);
    if (!inv_tps.valid) return -1;

    // Create 600x600 output
    const int OUT_SIZE = 600;
    cv::Mat output(OUT_SIZE, OUT_SIZE, CV_8UC3, cv::Scalar(0, 0, 0));

    // Map each output pixel back to camera pixel
    // Output coordinate system: center = (300, 300)
    // Normalized board coords: x = sin(angle_cw), y = cos(angle_cw)
    // We want 20 at top (12 o'clock), so y-axis points up in board space
    // In output image: row 0 = top, so we negate y
    double out_cx = OUT_SIZE / 2.0;
    double out_cy = OUT_SIZE / 2.0;
    double out_scale = OUT_SIZE / 2.0;  // 1.0 normalized = 300 pixels

    for (int row = 0; row < OUT_SIZE; ++row) {
        for (int col = 0; col < OUT_SIZE; ++col) {
            // Convert output pixel to normalized board coords
            double norm_x = (col - out_cx) / out_scale;
            double norm_y = (row - out_cy) / out_scale;
            // Note: In build_tps_transform, dst_y = norm_radius * cos(angle_cw)
            // and dst_x = norm_radius * sin(angle_cw)
            // So norm_x = sin(angle), norm_y = cos(angle) with 20 at y=+1 (top)
            // But in image space, row 0 is top, so we need norm_y inverted
            double board_x = norm_x;
            double board_y = -norm_y;  // flip Y so 20 (positive y in board) is at top of image

            // Skip pixels outside the board (with some margin)
            double dist = std::sqrt(board_x * board_x + board_y * board_y);
            if (dist > 1.15) continue;

            // Inverse warp: normalized board -> camera pixel
            Point2f px = inv_tps.transform(board_x, board_y);

            int src_x = (int)std::round(px.x);
            int src_y = (int)std::round(px.y);

            if (src_x >= 0 && src_x < input_img.cols && src_y >= 0 && src_y < input_img.rows) {
                output.at<cv::Vec3b>(row, col) = input_img.at<cv::Vec3b>(src_y, src_x);
            }
        }
    }

    // Draw spider overlay
    draw_spider_overlay(output, OUT_SIZE);

    // Encode to JPEG
    std::vector<unsigned char> out_buf;
    std::vector<int> params = {cv::IMWRITE_JPEG_QUALITY, 85};
    if (!cv::imencode(".jpg", output, out_buf, params)) return -1;

    if ((int)out_buf.size() > output_size) return -1;

    std::memcpy(output_jpeg, out_buf.data(), out_buf.size());
    *output_len = (int)out_buf.size();

    return 0;
}

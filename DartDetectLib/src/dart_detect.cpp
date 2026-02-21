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
                cv::Mat current = decode_image(current_images[task.index], current_sizes[task.index]);
                cv::Mat before = decode_image(before_images[task.index], before_sizes[task.index]);
                if (current.empty() || before.empty()) return std::nullopt;

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
                 << json_double("total_error", tri->total_error);
            
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
                     << json_double("barrel_aspect", det.barrel_aspect_ratio)
                     << "}";
                first_det = false;
            }
            json << "}";
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

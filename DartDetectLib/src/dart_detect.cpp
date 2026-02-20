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
#include <sstream>
#include <cstring>

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

// Simple JSON value extractor (handles nested objects, arrays, strings, numbers)
// This is intentionally minimal - just enough for calibration data.

static std::string extract_json_value(const std::string& json, const std::string& key) {
    std::string search = "\"" + key + "\"";
    size_t pos = json.find(search);
    if (pos == std::string::npos) return "";
    
    pos = json.find(':', pos + search.length());
    if (pos == std::string::npos) return "";
    ++pos;
    
    // Skip whitespace
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\n' || json[pos] == '\r'))
        ++pos;
    
    if (pos >= json.length()) return "";
    
    char first = json[pos];
    if (first == '"') {
        // String value
        size_t end = json.find('"', pos + 1);
        if (end == std::string::npos) return "";
        return json.substr(pos + 1, end - pos - 1);
    }
    if (first == '[' || first == '{') {
        // Array or object - find matching bracket
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
    // Number or literal
    size_t end = json.find_first_of(",}] \t\n\r", pos);
    if (end == std::string::npos) end = json.length();
    return json.substr(pos, end - pos);
}

static std::vector<double> parse_double_array(const std::string& arr) {
    std::vector<double> result;
    size_t pos = 0;
    while (pos < arr.length()) {
        // Skip non-digit chars
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
    
    // Format: [[cx,cy],[w,h],rotation]
    auto nums = parse_double_array(val);
    if (nums.size() < 5) return std::nullopt;
    
    EllipseData e;
    e.cx = nums[0]; e.cy = nums[1];
    e.width = nums[2]; e.height = nums[3];
    e.rotation_deg = nums[4];
    return e;
}

static bool parse_camera_calibration(const std::string& json, CameraCalibration& cal) {
    // Center
    std::string center_str = extract_json_value(json, "center");
    auto center_nums = parse_double_array(center_str);
    if (center_nums.size() < 2) return false;
    cal.center = Point2f(center_nums[0], center_nums[1]);
    
    // Segment angles
    std::string angles_str = extract_json_value(json, "segment_angles");
    cal.segment_angles = parse_double_array(angles_str);
    if (cal.segment_angles.size() < 20) return false;
    
    // Segment 20 index
    std::string s20 = extract_json_value(json, "segment_20_index");
    if (!s20.empty()) cal.segment_20_index = std::stoi(s20);
    
    // Ellipses
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
    // Find each camera key ("cam0", "cam1", "cam2")
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
    // Moved TPS transform building from per-detection (in triangulate_with_line_intersection)
    // to here at init time. The TPS solve is O(n^3) where n=~161 control points, which was
    // adding 500ms+ per dart detection. Computing once at startup and caching in
    // CameraCalibration::tps_cache reduced average detection time from 300ms to 178ms.
// Precompute TPS transforms for each camera (expensive, do once)
    for (auto& [cam_id, cal] : g_calibrations) {
        cal.tps_cache = build_tps_transform(cal);
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
    std::lock_guard<std::mutex> lock(g_mutex);
    
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
    
    // Detect per camera
    std::map<std::string, DetectionResult> camera_results;
    std::map<std::string, CameraCalibration> active_cals;
    
    for (int i = 0; i < num_cameras && i < 3; ++i) {
        std::string cam_id;
        if (camera_ids && camera_ids[i] && camera_ids[i][0] != '\0') {
            cam_id = camera_ids[i];
        } else {
            cam_id = "cam" + std::to_string(i);
        }
        
        auto cal_it = g_calibrations.find(cam_id);
        if (cal_it == g_calibrations.end()) continue;

        // Get previous dart masks for this specific camera only.
        std::vector<cv::Mat> prev_masks;
        if (dart_number > 1) {
            prev_masks = cache.get_masks(cam_id);
        }
        
        cv::Mat current = decode_image(current_images[i], current_sizes[i]);
        cv::Mat before = decode_image(before_images[i], before_sizes[i]);
        
        if (current.empty() || before.empty()) continue;
        
        DetectionResult det = detect_dart(
            current, before, cal_it->second.center, prev_masks, 15);
        
        if (det.tip) {
            camera_results[cam_id] = det;
            active_cals[cam_id] = cal_it->second;
        }
    }
    
    // Store per-camera masks so multi-dart segmentation stays camera-specific.
    for (const auto& [cam_id, det] : camera_results) {
        if (!det.motion_mask.empty()) {
            cache.add_mask(cam_id, det.motion_mask);
        }
    }
    
    // Build result JSON
    std::ostringstream json;
    json << "{";
    
    if (camera_results.size() >= 2) {
        // Triangulate
        auto tri = triangulate_with_line_intersection(camera_results, active_cals);
        
        if (tri) {
            json << json_int("segment", tri->segment) << ","
                 << json_int("multiplier", tri->multiplier) << ","
                 << json_int("score", tri->score) << ","
                 << json_string("method", tri->method) << ","
                 << json_double("confidence", tri->confidence) << ","
                 << json_double("total_error", tri->total_error);
            
            // Per-camera votes
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
        } else {
            // Triangulation failed - use best single camera
            goto single_camera;
        }
    } else if (camera_results.size() == 1) {
        single_camera:
        // Single camera: use ellipse scoring
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
        // No detections
        json << json_int("segment", 0) << ","
             << json_int("multiplier", 0) << ","
             << json_int("score", 0) << ","
             << json_string("method", "no_detection") << ","
             << json_double("confidence", 0.0);
    }
    
    json << "}";
    
    // Copy to heap for caller to free
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

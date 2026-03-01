/**
 * cbfc.cpp - Phase 28: Camera Bias Field Correction
 *
 * Learn mode (CBFC_Mode=1): Log single-camera projections for bias map building.
 * Apply mode (CBFC_Mode=2): Load bias map, correct single-cam candidate theta before WHRS.
 *
 * Bias correction applies ONLY to single-camera hypotheses.
 */
#include "dart_detect_internal.h"
#include <fstream>
#include <sstream>
#include <algorithm>
#include <cmath>
#include <mutex>
#include <vector>
#include <map>
#include <string>

// ============================================================================
// Feature Flags
// ============================================================================
static bool g_use_cbfc = false;
static bool g_cbfc_enable_smoothing = true;
static int  g_cbfc_mode = 0;  // 0=off, 1=learn, 2=apply

static const int RADIUS_BIN_COUNT = 6;
static const int ANGLE_BIN_COUNT = 20;
static const double MAX_BIAS_CORRECTION_DEG = 2.0;

// ============================================================================
// Bias Map: bias_map[camera][radius_bin][angle_bin] = correction_deg
// ============================================================================
static std::map<std::string, std::vector<std::vector<double>>> g_bias_map;
static bool g_bias_map_loaded = false;

// ============================================================================
// Learn Mode: accumulate single-cam projections to log file
// ============================================================================
static std::mutex g_learn_mutex;
static std::ofstream g_learn_log;
static bool g_learn_log_opened = false;

static const char* LEARN_LOG_PATH = "C:\\Users\\clawd\\DartGameSystem\\debug_outputs\\cbfc_learn_log.jsonl";
static const char* BIAS_MAP_PATH = "C:\\Users\\clawd\\DartGameSystem\\debug_outputs\\cbfc_bias_map.json";

int set_cbfc_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseCameraBiasFieldCorrection") { g_use_cbfc = (value != 0); return 0; }
    if (s == "EnableBiasSmoothing") { g_cbfc_enable_smoothing = (value != 0); return 0; }
    if (s == "CBFC_Mode") {
        g_cbfc_mode = value;
        // When switching to learn mode, open the log file
        if (value == 1) {
            std::lock_guard<std::mutex> lock(g_learn_mutex);
            if (!g_learn_log_opened) {
                g_learn_log.open(LEARN_LOG_PATH, std::ios::trunc);
                g_learn_log_opened = true;
            }
        } else if (value == 2) {
            // Load bias map
            cbfc_load_bias_map();
        }
        return 0;
    }
    return -1;
}

bool is_cbfc_enabled() { return g_use_cbfc && g_cbfc_mode > 0; }
int get_cbfc_mode() { return g_cbfc_mode; }

// ============================================================================
// Learn Mode: Log a single-camera projection
// ============================================================================
void cbfc_log_single_cam_projection(
    const std::string& camera_id,
    double radius_norm,
    double theta_deg,
    double coord_x,
    double coord_y)
{
    if (g_cbfc_mode != 1) return;
    
    std::lock_guard<std::mutex> lock(g_learn_mutex);
    if (!g_learn_log.is_open()) return;
    
    g_learn_log << "{\"cam\":\"" << camera_id
                << "\",\"r\":" << radius_norm
                << ",\"theta\":" << theta_deg
                << ",\"x\":" << coord_x
                << ",\"y\":" << coord_y
                << "}\n";
    g_learn_log.flush();
}

// ============================================================================
// Load Bias Map from JSON
// ============================================================================
void cbfc_load_bias_map() {
    g_bias_map.clear();
    g_bias_map_loaded = false;
    
    std::ifstream f(BIAS_MAP_PATH);
    if (!f.is_open()) return;
    
    // Simple JSON parser for our specific format:
    // {"cam1": [[b00,b01,...],[b10,b11,...],...], "cam2": ...}
    std::string content((std::istreambuf_iterator<char>(f)),
                         std::istreambuf_iterator<char>());
    f.close();
    
    // Parse camera entries
    size_t pos = 0;
    while (true) {
        // Find next camera key
        size_t q1 = content.find('"', pos);
        if (q1 == std::string::npos) break;
        size_t q2 = content.find('"', q1 + 1);
        if (q2 == std::string::npos) break;
        std::string cam_id = content.substr(q1 + 1, q2 - q1 - 1);
        
        // Skip to the outer array '['
        size_t arr_start = content.find('[', q2);
        if (arr_start == std::string::npos) break;
        
        // Check if this is actually a nested array [[...],[...]]
        // or just metadata. Look for '[' after ':'
        size_t colon = content.find(':', q2);
        if (colon == std::string::npos) break;
        
        // Check what follows the colon
        size_t next_nonspace = content.find_first_not_of(" \t\n\r", colon + 1);
        if (next_nonspace == std::string::npos) break;
        
        if (content[next_nonspace] != '[') {
            // Not an array, skip to next entry
            pos = next_nonspace;
            continue;
        }
        
        // Parse 2D array: [[v,v,...],[v,v,...],...]
        std::vector<std::vector<double>> bins(RADIUS_BIN_COUNT, std::vector<double>(ANGLE_BIN_COUNT, 0.0));
        
        size_t p = next_nonspace + 1; // past outer '['
        int rbin = 0;
        while (rbin < RADIUS_BIN_COUNT && p < content.size()) {
            // Find inner '['
            size_t inner_start = content.find('[', p);
            if (inner_start == std::string::npos) break;
            size_t inner_end = content.find(']', inner_start);
            if (inner_end == std::string::npos) break;
            
            // Parse values between [ and ]
            std::string inner = content.substr(inner_start + 1, inner_end - inner_start - 1);
            std::istringstream iss(inner);
            int abin = 0;
            double val;
            char comma;
            while (abin < ANGLE_BIN_COUNT && iss >> val) {
                bins[rbin][abin] = val;
                abin++;
                iss >> comma; // eat comma
            }
            
            rbin++;
            p = inner_end + 1;
        }
        
        // Find the closing ']' of outer array
        size_t outer_end = content.find(']', p);
        if (outer_end != std::string::npos) pos = outer_end + 1;
        else pos = p;
        
        g_bias_map[cam_id] = bins;
    }
    
    g_bias_map_loaded = !g_bias_map.empty();
}

// ============================================================================
// Apply Bias Correction to single-cam candidates
// ============================================================================
void cbfc_correct_candidates(std::vector<HhsCandidateExport>& candidates) {
    if (g_cbfc_mode != 2 || !g_use_cbfc || !g_bias_map_loaded) return;
    
    for (auto& c : candidates) {
        // Only correct single-camera candidates
        if (c.type.substr(0, 7) != "single_") continue;
        
        // Extract camera ID from type "single_CamX"
        std::string cam_id = c.type.substr(7);
        
        auto it = g_bias_map.find(cam_id);
        if (it == g_bias_map.end()) continue;
        
        // Compute bins from candidate position
        double radius_norm = c.radius;
        double theta_deg = c.theta_deg;
        
        int rbin = (int)(radius_norm * RADIUS_BIN_COUNT);
        if (rbin < 0) rbin = 0;
        if (rbin >= RADIUS_BIN_COUNT) rbin = RADIUS_BIN_COUNT - 1;
        
        int abin = (int)(theta_deg / (360.0 / ANGLE_BIN_COUNT));
        if (abin < 0) abin = 0;
        if (abin >= ANGLE_BIN_COUNT) abin = ANGLE_BIN_COUNT - 1;
        
        double bias = it->second[rbin][abin];
        
        // Clamp correction
        if (bias > MAX_BIAS_CORRECTION_DEG) bias = MAX_BIAS_CORRECTION_DEG;
        if (bias < -MAX_BIAS_CORRECTION_DEG) bias = -MAX_BIAS_CORRECTION_DEG;
        
        if (std::abs(bias) < 0.001) continue;
        
        // Apply correction: adjust theta
        double corrected_theta = theta_deg - bias;
        
        // Convert back to coords
        double corrected_theta_rad = corrected_theta * CV_PI / 180.0;
        // theta_deg was computed as atan2(y, -x) * 180/PI, adjusted to 0-360
        // To convert back: x = -r*cos(theta_rad), y = r*sin(theta_rad)
        c.coords.x = -radius_norm * std::cos(corrected_theta_rad);
        c.coords.y =  radius_norm * std::sin(corrected_theta_rad);
        
        // Re-score the corrected position
        c.theta_deg = std::fmod(corrected_theta + 360.0, 360.0);
        
        // Re-compute score for new position
        double dist = std::sqrt(c.coords.x * c.coords.x + c.coords.y * c.coords.y);
        c.score = score_from_polar(c.theta_deg, dist);
        c.radius = dist;
    }
}

// ============================================================================
// Flush learn log (called at end of process or explicitly)
// ============================================================================
void cbfc_flush_learn_log() {
    std::lock_guard<std::mutex> lock(g_learn_mutex);
    if (g_learn_log.is_open()) {
        g_learn_log.close();
        g_learn_log_opened = false;
    }
}

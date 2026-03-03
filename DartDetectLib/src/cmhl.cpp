/**
 * cmhl.cpp - Phase 42B: Conservative Miss Handling Layer
 *
 * Intercepts S0x0 (Miss) classifications at the end of the pipeline
 * and checks if there's enough barrel evidence to rescue them as actual scores.
 * Uses existing HHS candidates (g_hhs_candidates) for rescue hypotheses.
 *
 * Feature flag: UseCMHLFlag (default OFF)
 */
#include "dart_detect_internal.h"
#include <algorithm>
#include <cmath>
#include <fstream>
#include <mutex>
#include <string>
#include <sstream>
#include <vector>

// ============================================================================
// Feature Flag
// ============================================================================
static bool g_use_cmhl = false;

// ============================================================================
// Global tracking for ghost hit safety cap
// ============================================================================
static int g_cmhl_total_darts = 0;
static int g_cmhl_rescue_count = 0;
static std::mutex g_cmhl_mutex;

// ============================================================================
// Access HHS candidates (defined in hhs.cpp)
// ============================================================================
struct HhsCandidateExport {
    std::string type;
    Point2f coords;
    double radius;
    double theta_deg;
    ScoreResult score;
    double weighted_median_residual;
    int inlier_camera_count;
    int axis_support_count;
    double sum_qi;
    double max_qi;
    int cameras_used;
    double radial_delta_from_tri;
    double ring_boundary_distance;
    std::map<std::string, double> reproj_error_per_cam;
};
extern std::vector<HhsCandidateExport> g_hhs_candidates;

// ============================================================================
// Constants
// ============================================================================
static const double CMHL_QUALITY_THRESHOLD = 0.45;
static const double CMHL_RELAXATION_FACTOR = 1.10;  // 10% relaxation
static const double CMHL_BOARD_RADIUS_MAX = 1.03;   // normalized
static const double CMHL_MAX_RESCUE_RATE = 0.08;    // 8% cap
// Normal WHRS/HHS threshold for weighted_median_residual acceptance
// HHS uses g_hhs_R1 = 1.5px => 0.015 in normalized. We use relaxed version.
static const double CMHL_NORMAL_RESIDUAL_THRESHOLD = 0.015;

// ============================================================================
// Flag setter
// ============================================================================
int set_cmhl_flag(const char* name, int value) {
    std::string s(name);
    if (s == "UseCMHLFlag") { g_use_cmhl = (value != 0); return 0; }
    if (s == "CMHL_Reset") {
        std::lock_guard<std::mutex> lock(g_cmhl_mutex);
        g_cmhl_total_darts = 0;
        g_cmhl_rescue_count = 0;
        return 0;
    }
    return -1;
}

bool is_cmhl_enabled() { return g_use_cmhl; }

// ============================================================================
// Logging
// ============================================================================
static void cmhl_log(const std::string& msg) {
    static std::mutex log_mutex;
    std::lock_guard<std::mutex> lock(log_mutex);
    std::ofstream f("C:\\Users\\clawd\\phase42b_miss_log.txt", std::ios::app);
    if (f.is_open()) {
        f << msg << "\n";
    }
}

// ============================================================================
// CMHL Miss Rescue
// ============================================================================
CmhlResult cmhl_try_rescue(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations)
{
    CmhlResult result;
    result.was_miss_candidate = false;
    result.rescue_attempted = false;
    result.rescue_successful = false;

    // Track total darts for rescue rate cap
    {
        std::lock_guard<std::mutex> lock(g_cmhl_mutex);
        g_cmhl_total_darts++;
    }

    // ========================================================================
    // Section 2: Miss Ambiguity Detection
    // ========================================================================
    bool has_barrel_evidence = false;
    bool has_quality_camera = false;
    int cameras_with_barrel = 0;
    double max_detection_quality = 0.0;

    for (const auto& [cam_id, det] : camera_results) {
        // Check barrel evidence: barrel_pixel_count > 0 indicates barrel mask exists
        if (det.barrel_pixel_count > 0) {
            has_barrel_evidence = true;
            cameras_with_barrel++;
        }
        // Also check barrel_quality_class — anything other than BARREL_ABSENT
        if (det.barrel_quality_class != "BARREL_ABSENT" && !det.barrel_quality_class.empty()) {
            has_barrel_evidence = true;
        }
        // Check detection quality (mask_quality as proxy)
        double quality = det.mask_quality;
        if (quality >= CMHL_QUALITY_THRESHOLD) {
            has_quality_camera = true;
        }
        if (quality > max_detection_quality) {
            max_detection_quality = quality;
        }
    }

    // If ALL barrel evidence is absent → true miss, no rescue
    if (!has_barrel_evidence) {
        std::ostringstream log;
        log << "CMHL: true_miss | no_barrel_evidence | cameras=" << camera_results.size()
            << " | max_quality=" << max_detection_quality;
        cmhl_log(log.str());
        return result;
    }

    // Check quality threshold
    if (!has_quality_camera) {
        std::ostringstream log;
        log << "CMHL: true_miss | low_quality | max_quality=" << max_detection_quality
            << " | barrel_cams=" << cameras_with_barrel;
        cmhl_log(log.str());
        return result;
    }

    // Flag as MissCandidate
    result.was_miss_candidate = true;

    // ========================================================================
    // Section 3: Relaxed Secondary Pass using HHS candidates
    // ========================================================================
    result.rescue_attempted = true;

    // Look at existing HHS candidates for any non-miss hypothesis
    const HhsCandidateExport* best_candidate = nullptr;
    double best_residual = 999.0;

    for (const auto& cand : g_hhs_candidates) {
        // Skip miss candidates (segment=0)
        if (cand.score.segment == 0) continue;

        // CONSERVATIVE: Only rescue with candidates seen by 3 cameras (tri)
        // or pair candidates with very high quality (sum_qi >= 2.0, axis >= 2)
        // Pair candidates have near-zero residuals (exact intersection) which
        // is misleading — they need extra quality evidence.
        bool is_tri = (cand.type.find("tri") != std::string::npos);
        bool is_quality_pair = (cand.cameras_used >= 2 && 
                                cand.axis_support_count >= 2 && 
                                cand.sum_qi >= 2.0);
        
        if (!is_tri && !is_quality_pair) continue;

        // For tri: use residual with relaxed threshold
        if (is_tri) {
            double relaxed_threshold = CMHL_NORMAL_RESIDUAL_THRESHOLD * CMHL_RELAXATION_FACTOR;
            if (cand.weighted_median_residual > relaxed_threshold) continue;
        }

        // Pick best by lowest residual (for tri) or highest sum_qi (for pair)
        double score = is_tri ? (1000.0 - cand.weighted_median_residual) : cand.sum_qi;
        if (score > best_residual) {
            best_residual = score;
            best_candidate = &cand;
        }
    }

    if (!best_candidate) {
        std::ostringstream log;
        log << "CMHL: miss_candidate | no_viable_hypothesis | hhs_candidates=" << g_hhs_candidates.size()
            << " | barrel_cams=" << cameras_with_barrel;
        cmhl_log(log.str());
        return result;
    }

    // ========================================================================
    // Section 5: Ghost Hit Safety
    // ========================================================================

    // Check board radius
    if (best_candidate->radius > CMHL_BOARD_RADIUS_MAX) {
        std::ostringstream log;
        log << "CMHL: rescue_rejected | radius=" << best_candidate->radius
            << " | max=" << CMHL_BOARD_RADIUS_MAX
            << " | S" << best_candidate->score.segment << "x" << best_candidate->score.multiplier;
        cmhl_log(log.str());
        return result;
    }

    // Check rescue rate cap
    {
        std::lock_guard<std::mutex> lock(g_cmhl_mutex);
        if (g_cmhl_total_darts > 10) {  // Only enforce after 10 darts
            double current_rate = (double)(g_cmhl_rescue_count + 1) / g_cmhl_total_darts;
            if (current_rate > CMHL_MAX_RESCUE_RATE) {
                std::ostringstream log;
                log << "CMHL: rescue_rejected | rate_cap | current_rate=" << current_rate
                    << " | rescues=" << g_cmhl_rescue_count << "/" << g_cmhl_total_darts;
                cmhl_log(log.str());
                return result;
            }
        }
    }

    // ========================================================================
    // Rescue successful!
    // ========================================================================
    result.rescue_successful = true;
    result.rescued_segment = best_candidate->score.segment;
    result.rescued_multiplier = best_candidate->score.multiplier;
    result.rescued_score = best_candidate->score.score;
    result.rescued_coords = best_candidate->coords;
    result.rescued_radius = best_candidate->radius;
    result.rescued_residual = best_candidate->weighted_median_residual;
    result.rescued_type = best_candidate->type;
    result.candidate_count = (int)g_hhs_candidates.size();

    // Update rescue count
    {
        std::lock_guard<std::mutex> lock(g_cmhl_mutex);
        g_cmhl_rescue_count++;
    }

    std::ostringstream log;
    log << "CMHL: RESCUED | S" << result.rescued_segment << "x" << result.rescued_multiplier
        << " | score=" << result.rescued_score
        << " | residual=" << best_residual
        << " | radius=" << best_candidate->radius
        << " | type=" << best_candidate->type
        << " | barrel_cams=" << cameras_with_barrel
        << " | hhs_candidates=" << g_hhs_candidates.size();
    cmhl_log(log.str());

    return result;
}

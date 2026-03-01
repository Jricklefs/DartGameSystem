"""Apply Phase 28 CBFC edits to DartDetectLib source files."""
import os

BASE = r"C:\Users\clawd\DartGameSystem\DartDetectLib"

# 1. Add CBFC declarations to dart_detect_internal.h
hdr = os.path.join(BASE, "include", "dart_detect_internal.h")
with open(hdr, 'r') as f:
    content = f.read()

cbfc_decl = """
// ============================================================================
// Phase 28: CBFC (Camera Bias Field Correction)
// ============================================================================
int set_cbfc_flag(const char* name, int value);
bool is_cbfc_enabled();
int get_cbfc_mode();
void cbfc_load_bias_map();
void cbfc_log_single_cam_projection(const std::string& camera_id, double radius_norm, double theta_deg, double coord_x, double coord_y);
void cbfc_correct_candidates(std::vector<HhsCandidateExport>& candidates);
void cbfc_flush_learn_log();

"""

content = content.replace('#endif /* DART_DETECT_INTERNAL_H */', cbfc_decl + '#endif /* DART_DETECT_INTERNAL_H */')
with open(hdr, 'w') as f:
    f.write(content)
print("Updated dart_detect_internal.h")

# 2. Add set_cbfc_flag to dd_set_flag dispatch in dart_detect.cpp
dd = os.path.join(BASE, "src", "dart_detect.cpp")
with open(dd, 'r') as f:
    content = f.read()

# Add cbfc flag dispatch before whrs
old_dispatch = "    return set_whrs_flag(flag_name, value);"
new_dispatch = """    r = set_cbfc_flag(flag_name, value);
    if (r == 0) return 0;
    return set_whrs_flag(flag_name, value);"""
content = content.replace(old_dispatch, new_dispatch)

# Wire CBFC into the pipeline: after hhs_select populates candidates, before whrs_select
# In learn mode: log single-cam projections
# In apply mode: correct candidates before WHRS scoring
old_whrs_call = """            if (is_whrs_enabled()) {
                auto whrs_override = whrs_select(*tri, camera_results, active_cals);"""
new_whrs_call = """            // Phase 28: CBFC - log or correct single-cam candidates
            if (is_cbfc_enabled()) {
                if (get_cbfc_mode() == 1) {
                    // Learn mode: log single-cam projections
                    for (const auto& cand : g_hhs_candidates) {
                        if (cand.type.substr(0, 7) == "single_") {
                            cbfc_log_single_cam_projection(
                                cand.type.substr(7),
                                cand.radius, cand.theta_deg,
                                cand.coords.x, cand.coords.y);
                        }
                    }
                } else if (get_cbfc_mode() == 2) {
                    // Apply mode: correct single-cam candidates before WHRS
                    cbfc_correct_candidates(g_hhs_candidates);
                }
            }
            if (is_whrs_enabled()) {
                auto whrs_override = whrs_select(*tri, camera_results, active_cals);"""
content = content.replace(old_whrs_call, new_whrs_call)

with open(dd, 'w') as f:
    f.write(content)
print("Updated dart_detect.cpp")

# 3. Update CMakeLists.txt to include cbfc.cpp
cmake = os.path.join(BASE, "CMakeLists.txt")
with open(cmake, 'r') as f:
    content = f.read()

content = content.replace("    src/whrs.cpp\n)", "    src/whrs.cpp\n    src/cbfc.cpp\n)")
with open(cmake, 'w') as f:
    f.write(content)
print("Updated CMakeLists.txt")

# 4. Need forward declaration of HhsCandidateExport before cbfc uses it
# It's already declared in hhs.cpp. We need it in the header or cbfc.cpp needs to include it.
# Actually, HhsCandidateExport is defined in hhs.cpp, not the header. We need to move it or forward-declare.
# Let's check if it's already in the header...
with open(hdr, 'r') as f:
    h = f.read()
if 'HhsCandidateExport' not in h.split('Phase 28')[0]:
    # Need to add it to header. Let's add the full struct before the CBFC section.
    # Actually, it's extern'd in whrs.cpp too. Let's just move the struct to the header.
    struct_def = """
// Phase 26: HHS candidate export (shared between hhs.cpp, whrs.cpp, cbfc.cpp)
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
extern int g_hhs_baseline_wedge;

"""
    # Insert before Phase 28 section
    h = h.replace('\n// ============================================================================\n// Phase 28:', struct_def + '\n// ============================================================================\n// Phase 28:')
    with open(hdr, 'w') as f:
        f.write(h)
    print("Added HhsCandidateExport to header")
    
    # Remove duplicate struct from hhs.cpp and whrs.cpp
    for fname in ['hhs.cpp', 'whrs.cpp']:
        fpath = os.path.join(BASE, "src", fname)
        with open(fpath, 'r') as f:
            src = f.read()
        
        if fname == 'hhs.cpp':
            # Remove struct definition and extern declarations
            src = src.replace("""struct HhsCandidateExport {
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
std::vector<HhsCandidateExport> g_hhs_candidates;
int g_hhs_baseline_wedge = -1;""",
"""// HhsCandidateExport moved to dart_detect_internal.h
std::vector<HhsCandidateExport> g_hhs_candidates;
int g_hhs_baseline_wedge = -1;""")
        
        if fname == 'whrs.cpp':
            # Remove the extern struct + extern declarations
            src = src.replace("""// Declared in hhs.cpp - the candidate list from last hhs_select call
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
extern int g_hhs_baseline_wedge;""",
"""// HhsCandidateExport moved to dart_detect_internal.h""")
        
        with open(fpath, 'w') as f:
            f.write(src)
        print(f"Updated {fname}")

print("All edits applied!")

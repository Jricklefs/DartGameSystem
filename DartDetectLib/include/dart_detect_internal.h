/**
 * DartDetectLib - Internal header (not exported)
 * 
 * Shared types and function declarations for internal modules.
 */
#ifndef DART_DETECT_INTERNAL_H
#define DART_DETECT_INTERNAL_H

#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/imgcodecs.hpp>
#include <string>
#include <vector>
#include <map>
#include <optional>
#include <functional>
#include <cmath>
#include <mutex>

// Phase 3: Enable/disable ROI cropping (gate for safety)
#define ENABLE_ROI_CROP

// ============================================================================
// Constants
// ============================================================================

// Dartboard segments clockwise from top
static const int SEGMENT_ORDER[20] = {
    20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5
};

// Standard dartboard radii (mm from center)
static const double BULLSEYE_RADIUS_MM     = 6.35;
static const double OUTER_BULL_RADIUS_MM   = 16.0;
static const double TRIPLE_INNER_RADIUS_MM = 99.0;
static const double TRIPLE_OUTER_RADIUS_MM = 107.0;
static const double DOUBLE_INNER_RADIUS_MM = 162.0;
static const double DOUBLE_OUTER_RADIUS_MM = 170.0;

// Normalized radii (relative to outer double = 1.0)
static const double BULLSEYE_NORM     = BULLSEYE_RADIUS_MM / DOUBLE_OUTER_RADIUS_MM;
static const double OUTER_BULL_NORM   = OUTER_BULL_RADIUS_MM / DOUBLE_OUTER_RADIUS_MM;
static const double TRIPLE_INNER_NORM = TRIPLE_INNER_RADIUS_MM / DOUBLE_OUTER_RADIUS_MM;
static const double TRIPLE_OUTER_NORM = TRIPLE_OUTER_RADIUS_MM / DOUBLE_OUTER_RADIUS_MM;
static const double DOUBLE_INNER_NORM = DOUBLE_INNER_RADIUS_MM / DOUBLE_OUTER_RADIUS_MM;
static const double DOUBLE_OUTER_NORM = 1.0;

// Detection parameters (base values at 1080p; scaled by resolution_scale at runtime)
static const int BLOB_CHAIN_DIST_BASE = 150;
static const int MORPH_CLOSE_KERNEL_SIZE_BASE = 7;
static const int LINE_ABSORB_PERP_DIST = 20;
static const int LINE_ABSORB_EXTEND_LIMIT = 80;
static const int PCA_GAP_TOLERANCE = 120;
static const int PCA_MAX_WALK = 500;
static const int PCA_PERP_TOLERANCE = 15;
static const double DETECTION_MIN_NEW_DART_PIXEL_RATIO = 0.6;
static const int MOVED_PIXEL_DISTANCE = 15;

// Phase 3: Resolution-adaptive thresholds — base values at 1080p
static const double MASK_QUALITY_THRESHOLD_BASE = 12000.0;
static const double BARREL_WIDTH_MAX_BASE = 20.0;
static const double DART_LENGTH_MIN_BASE = 150.0;
static const double RANSAC_THRESHOLD_BASE = 3.5;
static const double RANSAC_MIN_PAIR_DIST_BASE = 20.0;

// Legacy aliases for backward compat (used where scale not yet applied)
static const int BLOB_CHAIN_DIST = 150;
static const int MORPH_CLOSE_KERNEL_SIZE = 7;

// ============================================================================
// Phase 3: Resolution Scale Helper
// ============================================================================

// Compute scale factor from image height relative to 1080p reference
inline double compute_resolution_scale(int image_height) {
    return (image_height > 0) ? (double)image_height / 1080.0 : 1.0;
}

// Scale a pixel value and ensure it's at least min_val
inline int scale_px(int base, double scale, int min_val = 1) {
    return std::max(min_val, (int)std::round(base * scale));
}

// Scale a pixel value and ensure it's odd (for kernel sizes)
inline int scale_px_odd(int base, double scale, int min_val = 3) {
    int v = std::max(min_val, (int)std::round(base * scale));
    return (v % 2 == 0) ? v + 1 : v;
}

inline double scale_d(double base, double scale) {
    return base * scale;
}

// ============================================================================
// Types
// ============================================================================

struct Point2f {
    double x, y;
    Point2f() : x(0), y(0) {}
    Point2f(double x_, double y_) : x(x_), y(y_) {}
};

struct PcaLine {
    double vx, vy;      // direction (normalized, vy > 0)
    double x0, y0;      // origin point
    double elongation;
    std::string method;
};

struct BarrelInfo {
    Point2f centroid;
    Point2f pivot;
    int area;
};

struct DetectionResult {
    std::optional<Point2f> tip;
    double confidence = 0.0;
    std::optional<PcaLine> pca_line;
    double dart_length = 0.0;
    std::string method = "none";
    double view_quality = 0.5;
    double mask_quality = 1.0;
    cv::Mat motion_mask;  // for board cache

    // Phase 4A: Detection quality metrics for consensus weighting
    double ransac_inlier_ratio = 0.0;
    int barrel_pixel_count = 0;
    double barrel_aspect_ratio = 0.0;

    // Phase 9: Ridge/centerline barrel metrics
    int ridge_point_count = 0;
    double ridge_inlier_ratio = 0.0;
    double ridge_mean_perp_residual = 0.0;
    double mean_thickness_px = 0.0;
    double thickness_p90_px = 0.0;
    double shaft_length_px = 0.0;
    int barrel_candidate_pixel_count = 0;
    int flight_exclusion_removed_px = 0;
    std::string barrel_quality_class = "BARREL_ABSENT";
    bool tip_ahead_of_flight = false;
    bool tip_swap_applied = false;
    double angle_line_vs_pca_deg = -1.0;
    double angle_line_vs_flighttip_deg = -1.0;
    std::string line_fit_method_p9;
};

struct ScoreResult {
    int segment = 0;
    int multiplier = 0;
    int score = 0;
    std::string zone;
    double boundary_distance_deg = 0.0;
    double confidence = 0.0;
};

struct IntersectionResult {
    int segment = 0;
    int multiplier = 0;
    int score = 0;
    std::string method;
    double confidence = 0.0;
    Point2f coords;
    double total_error = 0.0;
    std::map<std::string, ScoreResult> per_camera;

    // Phase 7: Debug export fields for triangulation confidence
    struct TriangulationDebug {
        struct CamDebug {
            double warped_dir_x = 0.0;
            double warped_dir_y = 0.0;
            double perp_residual = 0.0;
            int barrel_pixel_count = 0;
            double barrel_aspect_ratio = 0.0;
            double detection_quality = 0.0;
            bool weak_barrel_signal = false;
            double warped_point_x = 0.0;
            double warped_point_y = 0.0;
        };
        std::map<std::string, CamDebug> cam_debug;
        double angle_spread_deg = 0.0;
        double median_residual = 0.0;
        double max_residual = 0.0;
        double residual_spread = 0.0;
        double final_confidence = 0.0;
        double board_radius = 0.0;
        std::string radius_gate_reason;
        bool segment_label_corrected = false;
        bool camera_dropped = false;
        std::string dropped_cam_id;
        // Wire boundary voting debug
        double boundary_distance_deg = 0.0;
        bool is_wire_ambiguous = false;
        std::string wedge_chosen_by = "direct";
        int base_wedge = -1;
        int neighbor_wedge = -1;
        std::map<int, int> wedge_votes;
        double winner_pct = 0.0;
        double vote_margin = 0.0;
        std::string low_conf_reason;
        // Phase 10B: Radial Stability Clamp
        bool radial_clamp_applied = false;
        std::string radial_clamp_reason;
        double r_bcwt = 0.0;
        double r_bestpair = 0.0;
        double radial_delta = 0.0;
        bool near_ring_bcwt = false;
        bool near_ring_best = false;
        bool near_ring_any = false;
        double x_preclamp_x = 0.0;
        double x_preclamp_y = 0.0;
        double x_bestpair_x = 0.0;
        double x_bestpair_y = 0.0;
    };
    std::optional<TriangulationDebug> tri_debug;
};

// Ellipse calibration data per camera
struct EllipseData {
    double cx, cy;
    double width, height;
    double rotation_deg;
};

// TPS (Thin-Plate Spline) transform data
struct TpsTransform {
    cv::Mat src_points;  // Nx2 source (pixel) control points
    cv::Mat dst_points;  // Nx2 destination (normalized board) control points
    cv::Mat weights;     // TPS weights (Nx2 + 3x2)
    bool valid = false;
    
    // Transform a point from pixel to normalized board space
    Point2f transform(double px, double py) const;
};

struct CameraCalibration {
    Point2f center;
    std::vector<double> segment_angles;  // 20 boundary angles (radians)
    int segment_20_index = 0;
    std::optional<EllipseData> outer_double_ellipse;
    std::optional<EllipseData> inner_double_ellipse;
    std::optional<EllipseData> outer_triple_ellipse;
    std::optional<EllipseData> inner_triple_ellipse;
    std::optional<EllipseData> bull_ellipse;
    std::optional<EllipseData> bullseye_ellipse;
    
    // Precomputed TPS transform (built once at init, not per-detection).
    TpsTransform tps_cache;
    
    // Phase 3: Board ROI — bounding rect of outer double ellipse + margin
    cv::Rect board_roi;       // ROI in full-image space
    bool has_roi = false;
    
    // Phase 3: Resolution scale factor (image_height / 1080.0)
    double resolution_scale = 1.0;
};

// Board cache: stores previous dart masks for multi-dart detection
struct BoardCache {
    std::map<std::string, std::vector<cv::Mat>> prev_dart_masks_by_camera;  // camera_id -> masks from darts 1..N-1
    std::mutex mtx;
    
    void clear() {
        std::lock_guard<std::mutex> lock(mtx);
        prev_dart_masks_by_camera.clear();
    }
    
    void add_mask(const std::string& camera_id, const cv::Mat& mask) {
        std::lock_guard<std::mutex> lock(mtx);
        prev_dart_masks_by_camera[camera_id].push_back(mask.clone());
    }
    
    std::vector<cv::Mat> get_masks(const std::string& camera_id) {
        std::lock_guard<std::mutex> lock(mtx);
        auto it = prev_dart_masks_by_camera.find(camera_id);
        if (it == prev_dart_masks_by_camera.end()) return {};
        return it->second;
    }
};

// ============================================================================
// Module: mask.h - Motion mask computation & board cache
// ============================================================================

struct MotionMaskResult {
    cv::Mat mask;           // Final hysteresis mask
    cv::Mat high_mask;      // High-threshold mask
    cv::Mat positive_mask;  // Positive (appeared) pixels only
};

MotionMaskResult compute_motion_mask(
    const cv::Mat& current,
    const cv::Mat& previous,
    int blur_size = 5,
    int threshold = 20
);

struct PixelSegmentation {
    cv::Mat new_mask;
    cv::Mat old_mask;
    cv::Mat moved_mask;
    cv::Mat stationary_mask;
    cv::Mat full_motion_mask;
    int new_count = 0;
    int old_count = 0;
    int moved_count = 0;
    int stationary_count = 0;
    double new_dart_pixel_ratio = 0.0;
};

PixelSegmentation compute_pixel_segmentation(
    const cv::Mat& current,
    const cv::Mat& previous,
    const std::vector<cv::Mat>& prev_dart_masks,
    int threshold = 20,
    int blur_size = 5,
    const MotionMaskResult* precomputed_mmr = nullptr
);

cv::Mat shape_filter(const cv::Mat& mask, double min_aspect = 2.0, int min_area = 100);

// ============================================================================
// Module: skeleton.h - Skeleton/Hough detection & tip finding
// ============================================================================

DetectionResult detect_dart(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    Point2f board_center,
    const std::vector<cv::Mat>& prev_dart_masks,
    int diff_threshold = 20,
    double resolution_scale = 1.0
);

// ============================================================================
// Module: scoring.h - Segment scoring & voting
// ============================================================================

ScoreResult score_from_polar(double angle_deg, double norm_dist);

ScoreResult score_from_ellipse_calibration(
    double tip_x, double tip_y,
    const CameraCalibration& cal
);

double ellipse_radius_at_angle(const EllipseData& ellipse, double angle_rad);

// ============================================================================
// Module: triangulation.h - Line intersection + TPS homography
// ============================================================================

TpsTransform build_tps_transform(const CameraCalibration& cal);

Point2f warp_point(const TpsTransform& tps, double px, double py);

std::optional<Point2f> intersect_lines_2d(
    double x1, double y1, double x2, double y2,
    double x3, double y3, double x4, double y4
);

std::optional<IntersectionResult> triangulate_with_line_intersection(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations
);

// ============================================================================
// Utility
// ============================================================================

// Parse calibration JSON into CameraCalibration structs
bool parse_calibrations(const std::string& json,
                        std::map<std::string, CameraCalibration>& out);

// Decode image bytes (JPEG/PNG) into cv::Mat
cv::Mat decode_image(const unsigned char* data, int size);


// ============================================================================
// Module: pca_detection.h - PCA barrel line detection (dual pipeline)
// ============================================================================

std::optional<PcaLine> detect_barrel_pca(
    const cv::Mat& current,
    const cv::Mat& previous,
    double otsu_fraction = 0.26,
    int morph_kernel_size = 5,
    double min_elongation = 2.0,
    int min_contour_area = 50
);

std::optional<IntersectionResult> triangulate_pca(
    const std::map<std::string, std::optional<PcaLine>>& pca_lines,
    const std::map<std::string, CameraCalibration>& calibrations
);


// ============================================================================
// Module: iqdl.h - Phase 17: IQDL Enhanced Tip Detection
// ============================================================================

struct IqdlResult {
    bool valid = false;
    bool fallback = true;
    
    Point2f tip_px;            // integer tip
    Point2f tip_px_subpixel;   // subpixel tip
    double W_i = 0.0;         // confidence weight
    double Q = 0.0;           // quality score
    
    // Shaft axis
    double shaft_vx = 0, shaft_vy = 0;
    double shaft_x0 = 0, shaft_y0 = 0;
    int inlier_count = 0;
    double axis_length = 0;
    
    // Quality metrics
    double sharpness = 0;
    double edge_energy = 0;
    int dart_area = 0;
    int blob_count = 0;
    
    // For triangulation
    std::optional<PcaLine> pca_line;
};

IqdlResult run_iqdl(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    const cv::Mat& motion_mask,
    Point2f board_center,
    double resolution_scale = 1.0
);


IqdlResult iqdl_refine_tip(
    const cv::Mat& current_frame,
    const cv::Mat& previous_frame,
    const cv::Mat& motion_mask,
    Point2f board_center,
    Point2f legacy_tip,
    const std::optional<PcaLine>& legacy_line,
    double resolution_scale = 1.0
);


// ============================================================================
// Module: sap.h - Phase 19B: Soft Accept Prevention
// ============================================================================

struct SapResult {
    bool baseline_would_miss = false;
    int relaxed_cam_count = 0;
    std::string relaxed_cam_ids;
    double theta_spread_relaxed = 0.0;
    double residual_soft = 0.0;
    bool board_containment_pass = false;
    bool angular_gate_pass = false;
    bool residual_gate_pass = false;
    bool soft_accept_applied = false;
    int final_segment = 0;
    int final_multiplier = 0;
    int final_score = 0;
    std::optional<IntersectionResult> override_result;
};

SapResult run_sap(
    const std::map<std::string, DetectionResult>& camera_results,
    const std::map<std::string, CameraCalibration>& calibrations,
    const std::map<std::string, IqdlResult>& iqdl_results,
    const IntersectionResult* baseline_result
);


#endif /* DART_DETECT_INTERNAL_H */
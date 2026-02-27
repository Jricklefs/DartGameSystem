/**
 * DartDetectLib - Native C++ dart detection library
 * 
 * Ported from Python DartDetect (skeleton_detection.py + routes.py)
 * Called from C#/.NET 8 via P/Invoke
 * 
 * Pipeline: motion mask ΓåÆ shape filter ΓåÆ skeleton/Hough ΓåÆ barrel detection ΓåÆ
 *           PCA blob chain tip ΓåÆ line intersection triangulation ΓåÆ scoring ΓåÆ voting
 */
#ifndef DART_DETECT_H
#define DART_DETECT_H

#ifdef _WIN32
    #ifdef DART_DETECT_EXPORTS
        #define DD_API __declspec(dllexport)
    #else
        #define DD_API __declspec(dllimport)
    #endif
#else
    #define DD_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Initialize the library with calibration data for all cameras.
 * 
 * @param calibration_json JSON string containing per-camera calibration:
 *   {
 *     "cam0": { "center": [cx,cy], "segment_angles": [...], "segment_20_index": N,
 *               "outer_double_ellipse": [[cx,cy],[w,h],rot], ... },
 *     "cam1": { ... },
 *     "cam2": { ... }
 *   }
 * @return 0 on success, negative on error
 */
DD_API int dd_init(const char* calibration_json);

/**
 * Process a dart detection across all cameras.
 * 
 * @param dart_number 1-based dart number in the current turn (1, 2, or 3)
 * @param board_id Board identifier string
 * @param num_cameras Number of cameras (typically 3)
 * @param camera_ids Array of camera ID strings aligned to image arrays
 * @param current_images Array of pointers to JPEG/PNG encoded image bytes per camera
 * @param current_sizes Array of byte sizes for each current image
 * @param before_images Array of pointers to baseline image bytes per camera
 * @param before_sizes Array of byte sizes for each baseline image
 * @return JSON string with detection result. Caller must free with dd_free_string().
 *   {
 *     "segment": 20, "multiplier": 3, "score": 60,
 *     "method": "UnanimousCam", "confidence": 0.95,
 *     "per_camera": { "cam0": {...}, "cam1": {...}, "cam2": {...} }
 *   }
 */
DD_API const char* dd_detect(
    int dart_number,
    const char* board_id,
    int num_cameras,
    const char** camera_ids,
    const unsigned char** current_images,
    const int* current_sizes,
    const unsigned char** before_images,
    const int* before_sizes
);

/**
 * Initialize board cache for a new game.
 * Clears all stored masks and baselines.
 */
DD_API void dd_init_board(const char* board_id);

/**
 * Clear board cache (end of game / board change).
 */
DD_API void dd_clear_board(const char* board_id);

/**
 * Free a string returned by dd_detect().
 */
DD_API void dd_free_string(const char* str);

/**
 * Get library version string.
 */
DD_API const char* dd_version(void);


/**
 * Generate a front-on (top-down) warped view of the dartboard.
 * Uses the TPS warp to create a 600x600 image viewed from directly above.
 *
 * @param camera_index Camera index (0, 1, or 2)
 * @param input_jpeg   Pointer to input JPEG image bytes
 * @param input_len    Length of input JPEG data
 * @param output_jpeg  Buffer to receive output JPEG
 * @param output_len   Receives actual output JPEG length
 * @param output_size  Max size of output buffer
 * @return 0 on success, -1 on error
 */
DD_API int GetFrontonView(
    int camera_index,
    const unsigned char* input_jpeg, int input_len,
    unsigned char* output_jpeg, int* output_len,
    int output_size
);

/**
 * Set a feature flag.
 * @return 0 on success, -1 if flag not found
 */
DD_API int dd_set_flag(const char* flag_name, int value);

#ifdef __cplusplus
}
#endif

#endif /* DART_DETECT_H */

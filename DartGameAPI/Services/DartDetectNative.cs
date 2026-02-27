using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DartGameAPI.Services;

/// <summary>
/// P/Invoke wrapper for DartDetectLib native C++ library.
/// Replaces HTTP calls to DartDetect Python API with direct native calls.
/// </summary>
public static class DartDetectNative
{
    private const string LibName = "DartDetectLib";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dd_init([MarshalAs(UnmanagedType.LPUTF8Str)] string calibrationJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dd_detect(
        int dartNumber,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string boardId,
        int numCameras,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] cameraIds,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] currentImages,
        [MarshalAs(UnmanagedType.LPArray)] int[] currentSizes,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] beforeImages,
        [MarshalAs(UnmanagedType.LPArray)] int[] beforeSizes);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_init_board([MarshalAs(UnmanagedType.LPUTF8Str)] string boardId);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_clear_board([MarshalAs(UnmanagedType.LPUTF8Str)] string boardId);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_set_pca_enabled(int enabled);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_free_string(IntPtr str);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetFrontonView")]
    private static extern int dd_get_fronton_view(
        int cameraIndex,
        byte[] inputJpeg, int inputLen,
        byte[] outputJpeg, out int outputLen,
        int outputSize);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dd_version();

    /// <summary>
    /// Initialize the detection library with calibration data.
    /// Call once at startup or when calibrations change.
    /// </summary>
    /// <param name="calibrationJson">JSON with per-camera calibration data</param>
    /// <returns>true on success</returns>
    public static bool Initialize(string calibrationJson)
    {
        return dd_init(calibrationJson) == 0;
    }

    /// <summary>
    /// Get library version string.
    /// </summary>
    public static string GetVersion()
    {
        var ptr = dd_version();
        return Marshal.PtrToStringUTF8(ptr) ?? "unknown";
    }

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dd_set_flag([MarshalAs(UnmanagedType.LPUTF8Str)] string flagName, int value);

    public static int SetFlag(string flagName, int value)
    {
        return dd_set_flag(flagName, value);
    }

    /// <summary>
    /// Detect a dart from camera images.
    /// </summary>
    /// <param name="dartNumber">1-based dart number in turn</param>
    /// <param name="boardId">Board identifier</param>
    /// <param name="currentImages">Current frame bytes per camera (JPEG/PNG encoded)</param>
    /// <param name="beforeImages">Baseline frame bytes per camera (JPEG/PNG encoded)</param>
    /// <returns>Detection result with segment, multiplier, score, confidence</returns>
    public static DetectionResult? Detect(
        int dartNumber,
        string boardId,
        List<string> cameraIds,
        byte[][] currentImages,
        byte[][] beforeImages)
    {
        if (currentImages.Length != beforeImages.Length ||
            currentImages.Length != cameraIds.Count ||
            currentImages.Length == 0)
            return null;

        int numCameras = currentImages.Length;

        // Pin image byte arrays and create pointer arrays
        var cameraIdPtrs = new IntPtr[numCameras];
        var currentPtrs = new IntPtr[numCameras];
        var beforePtrs = new IntPtr[numCameras];
        var currentSizes = new int[numCameras];
        var beforeSizes = new int[numCameras];
        var currentHandles = new GCHandle[numCameras];
        var beforeHandles = new GCHandle[numCameras];

        try
        {
            for (int i = 0; i < numCameras; i++)
            {
                cameraIdPtrs[i] = Marshal.StringToCoTaskMemUTF8(cameraIds[i]);
                currentHandles[i] = GCHandle.Alloc(currentImages[i], GCHandleType.Pinned);
                beforeHandles[i] = GCHandle.Alloc(beforeImages[i], GCHandleType.Pinned);
                currentPtrs[i] = currentHandles[i].AddrOfPinnedObject();
                beforePtrs[i] = beforeHandles[i].AddrOfPinnedObject();
                currentSizes[i] = currentImages[i].Length;
                beforeSizes[i] = beforeImages[i].Length;
            }

            IntPtr resultPtr = dd_detect(
                dartNumber, boardId, numCameras,
                cameraIdPtrs,
                currentPtrs, currentSizes,
                beforePtrs, beforeSizes);

            if (resultPtr == IntPtr.Zero)
                return null;

            string json = Marshal.PtrToStringUTF8(resultPtr) ?? "{}";
            dd_free_string(resultPtr);

            return JsonSerializer.Deserialize<DetectionResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        finally
        {
            for (int i = 0; i < numCameras; i++)
            {
                if (currentHandles[i].IsAllocated) currentHandles[i].Free();
                if (beforeHandles[i].IsAllocated) beforeHandles[i].Free();
                if (cameraIdPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(cameraIdPtrs[i]);
            }
        }
    }

    /// <summary>
    /// Enable or disable PCA dual pipeline.
    /// </summary>
    public static void SetPcaEnabled(bool enabled) => dd_set_pca_enabled(enabled ? 1 : 0);

    /// <summary>
    /// Initialize board cache for a new game.
    /// </summary>
    public static void InitBoard(string boardId) => dd_init_board(boardId);

    /// <summary>
    /// Clear board cache (end of game).
    /// </summary>
    public static void ClearBoard(string boardId) => dd_clear_board(boardId);

    /// <summary>
    /// Generate a front-on (top-down) warped view of the dartboard from a camera image.
    /// Returns the warped JPEG image bytes, or null on error.
    /// </summary>
    public static byte[]? GetFrontonViewImage(int cameraIndex, byte[] inputJpeg)
    {
        const int MAX_OUTPUT_SIZE = 2 * 1024 * 1024; // 2MB max output
        var outputBuffer = new byte[MAX_OUTPUT_SIZE];
        
        int result = dd_get_fronton_view(
            cameraIndex,
            inputJpeg, inputJpeg.Length,
            outputBuffer, out int outputLen,
            MAX_OUTPUT_SIZE);
        
        if (result != 0 || outputLen <= 0)
            return null;
        
        var output = new byte[outputLen];
        Array.Copy(outputBuffer, output, outputLen);
        return output;
    }
}

/// <summary>
/// Result from native dart detection.
/// </summary>
public class DetectionResult
{
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public string Method { get; set; } = "";
    public double Confidence { get; set; }
    public double TotalError { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, CameraVote>? PerCamera { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("coords_x")]
    public double CoordsX { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("coords_y")]
    public double CoordsY { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("debug_lines")]
    public Dictionary<string, CamDebugInfo>? DebugLines { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("pca_result")]
    public PcaResult? PcaResult { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("camera_details")]
    public Dictionary<string, CameraDetail>? CameraDetails { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("tri_debug")]
    public TriangulationDebugInfo? TriDebug { get; set; }
}

public class TriangulationDebugInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("angle_spread_deg")]
    public double AngleSpreadDeg { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("median_residual")]
    public double MedianResidual { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("max_residual")]
    public double MaxResidual { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("residual_spread")]
    public double ResidualSpread { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("final_confidence")]
    public double FinalConfidence { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("camera_dropped")]
    public bool CameraDropped { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("dropped_cam_id")]
    public string DroppedCamId { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("cam_debug")]
    public Dictionary<string, CamTriDebug>? CamDebug { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("board_radius")]
    public double BoardRadius { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("radius_gate_reason")]
    public string RadiusGateReason { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("segment_label_corrected")]
    public bool SegmentLabelCorrected { get; set; }
}

public class CamTriDebug
{
    [System.Text.Json.Serialization.JsonPropertyName("warped_dir_x")]
    public double WarpedDirX { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("warped_dir_y")]
    public double WarpedDirY { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("perp_residual")]
    public double PerpResidual { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("barrel_pixel_count")]
    public int BarrelPixelCount { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("barrel_aspect_ratio")]
    public double BarrelAspectRatio { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("detection_quality")]
    public double DetectionQuality { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("weak_barrel_signal")]
    public bool WeakBarrelSignal { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("warped_point_x")]
    public double WarpedPointX { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("warped_point_y")]
    public double WarpedPointY { get; set; }
}

public class CameraDetail
{
    [System.Text.Json.Serialization.JsonPropertyName("tip_method")]
    public string TipMethod { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("barrel_method")]
    public string BarrelMethod { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("mask_quality")]
    public double MaskQuality { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ransac_inlier_ratio")]
    public double RansacInlierRatio { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("barrel_aspect")]
    public double BarrelAspect { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tip_x")]
    public double TipX { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tip_y")]
    public double TipY { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("line_vx")]
    public double LineVx { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("line_vy")]
    public double LineVy { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("line_x0")]
    public double LineX0 { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("line_y0")]
    public double LineY0 { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("line_elongation")]
    public double LineElongation { get; set; }
}

public class CamDebugInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("ls_x")]
    public double LsX { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ls_y")]
    public double LsY { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("le_x")]
    public double LeX { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("le_y")]
    public double LeY { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tip_nx")]
    public double TipNx { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tip_ny")]
    public double TipNy { get; set; }
}


/// <summary>
/// PCA dual pipeline result.
/// </summary>
public class PcaResult
{
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public string Method { get; set; } = "";
    public double Confidence { get; set; }
}

/// <summary>
/// Per-camera vote result.
/// </summary>
public class CameraVote
{
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public string Zone { get; set; } = "";
}

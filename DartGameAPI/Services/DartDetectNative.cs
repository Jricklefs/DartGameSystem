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
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] currentImages,
        [MarshalAs(UnmanagedType.LPArray)] int[] currentSizes,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] beforeImages,
        [MarshalAs(UnmanagedType.LPArray)] int[] beforeSizes);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_init_board([MarshalAs(UnmanagedType.LPUTF8Str)] string boardId);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_clear_board([MarshalAs(UnmanagedType.LPUTF8Str)] string boardId);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dd_free_string(IntPtr str);

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
        byte[][] currentImages,
        byte[][] beforeImages)
    {
        if (currentImages.Length != beforeImages.Length || currentImages.Length == 0)
            return null;

        int numCameras = currentImages.Length;

        // Pin image byte arrays and create pointer arrays
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
                currentHandles[i] = GCHandle.Alloc(currentImages[i], GCHandleType.Pinned);
                beforeHandles[i] = GCHandle.Alloc(beforeImages[i], GCHandleType.Pinned);
                currentPtrs[i] = currentHandles[i].AddrOfPinnedObject();
                beforePtrs[i] = beforeHandles[i].AddrOfPinnedObject();
                currentSizes[i] = currentImages[i].Length;
                beforeSizes[i] = beforeImages[i].Length;
            }

            IntPtr resultPtr = dd_detect(
                dartNumber, boardId, numCameras,
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
            }
        }
    }

    /// <summary>
    /// Initialize board cache for a new game.
    /// </summary>
    public static void InitBoard(string boardId) => dd_init_board(boardId);

    /// <summary>
    /// Clear board cache (end of game).
    /// </summary>
    public static void ClearBoard(string boardId) => dd_clear_board(boardId);
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

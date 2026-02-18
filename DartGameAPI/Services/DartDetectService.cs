using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Data;
using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Service that wraps DartDetectNative (C++ library) for in-process dart detection.
/// Replaces HTTP calls to the Python DartDetect API.
/// Falls back to DartDetectClient (HTTP) if native lib is unavailable.
/// </summary>
public class DartDetectService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DartDetectService> _logger;
    private readonly DartDetectClient _httpClient;
    private bool _nativeAvailable;
    private bool _initialized;
    private readonly object _initLock = new();

    public DartDetectService(
        IServiceProvider serviceProvider,
        ILogger<DartDetectService> logger,
        DartDetectClient httpClient)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _httpClient = httpClient;

        // Try to load native lib
        try
        {
            var version = DartDetectNative.GetVersion();
            _nativeAvailable = true;
            _logger.LogInformation("DartDetectLib native loaded: {Version}", version);
        }
        catch (DllNotFoundException)
        {
            _nativeAvailable = false;
            _logger.LogWarning("DartDetectLib.dll not found - falling back to HTTP DartDetect API");
        }
        catch (Exception ex)
        {
            _nativeAvailable = false;
            _logger.LogWarning(ex, "Failed to load DartDetectLib - falling back to HTTP");
        }
    }

    public bool IsNativeAvailable => _nativeAvailable;

    /// <summary>
    /// Initialize native library with calibration data from DB.
    /// Call on startup and when calibrations change.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_nativeAvailable) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

            // Load all camera calibrations
            var calibrations = await db.Calibrations
                .GroupBy(c => c.CameraId)
                .Select(g => g.OrderByDescending(c => c.CreatedAt).First())
                .ToListAsync();

            // Build calibration JSON: { "cam0": {...}, "cam1": {...}, "cam2": {...} }
            var calDict = new Dictionary<string, object?>();
            foreach (var cal in calibrations)
            {
                if (cal.CalibrationData != null)
                {
                    try
                    {
                        var data = JsonSerializer.Deserialize<object>(cal.CalibrationData);
                        calDict[cal.CameraId] = data;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse calibration for {CameraId}", cal.CameraId);
                    }
                }
            }

            var json = JsonSerializer.Serialize(calDict);
            var success = DartDetectNative.Initialize(json);

            if (success)
            {
                _initialized = true;
                _logger.LogInformation("DartDetectLib initialized with {Count} camera calibrations", calDict.Count);
            }
            else
            {
                _logger.LogError("DartDetectLib initialization failed - falling back to HTTP");
                _nativeAvailable = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DartDetectLib");
            _nativeAvailable = false;
        }
    }

    /// <summary>
    /// Detect a dart using native C++ library or HTTP fallback.
    /// </summary>
    public async Task<DetectResponse?> DetectAsync(
        List<CameraImageDto> images,
        string boardId = "default",
        int dartNumber = 1,
        List<CameraImageDto>? beforeImages = null,
        CancellationToken ct = default)
    {
        if (_nativeAvailable && _initialized)
        {
            return await DetectNativeAsync(images, boardId, dartNumber, beforeImages);
        }

        // Fallback to HTTP
        return await _httpClient.DetectAsync(images, boardId, dartNumber, beforeImages, ct);
    }

    private Task<DetectResponse?> DetectNativeAsync(
        List<CameraImageDto> images,
        string boardId,
        int dartNumber,
        List<CameraImageDto>? beforeImages)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            int numCameras = images.Count;
            var currentBytes = new byte[numCameras][];
            var beforeBytes = new byte[numCameras][];

            for (int i = 0; i < numCameras; i++)
            {
                currentBytes[i] = Convert.FromBase64String(images[i].Image);
                beforeBytes[i] = beforeImages != null && i < beforeImages.Count
                    ? Convert.FromBase64String(beforeImages[i].Image)
                    : Array.Empty<byte>();
            }

            var decodeMs = sw.ElapsedMilliseconds;

            var result = DartDetectNative.Detect(dartNumber, boardId, currentBytes, beforeBytes);

            var totalMs = sw.ElapsedMilliseconds;
            _logger.LogInformation("[TIMING] Native detect: decode={DecodeMs}ms, total={TotalMs}ms", decodeMs, totalMs);

            if (result == null || result.Error != null)
            {
                _logger.LogWarning("Native detection failed: {Error}", result?.Error ?? "null result");
                return Task.FromResult<DetectResponse?>(null);
            }

            // Convert native DetectionResult to DetectResponse (Tips format)
            var tip = new DetectedTip
            {
                Segment = result.Segment,
                Multiplier = result.Multiplier,
                Score = result.Score,
                Zone = FormatZone(result.Segment, result.Multiplier),
                Confidence = result.Confidence,
                XMm = 0, // Native lib doesn't return mm coords yet
                YMm = 0,
                CamerasSeen = result.PerCamera?.Keys.ToList() ?? new List<string>()
            };

            var response = new DetectResponse
            {
                RequestId = Guid.NewGuid().ToString()[..8],
                ProcessingMs = (int)totalMs,
                Tips = result.Segment > 0 || result.Score > 0
                    ? new List<DetectedTip> { tip }
                    : new List<DetectedTip>(),
                CameraResults = result.PerCamera?.Select(kv => new CameraDetectionResult
                {
                    CameraId = kv.Key,
                    TipsDetected = 1
                }).ToList() ?? new List<CameraDetectionResult>()
            };

            _logger.LogInformation("[NATIVE] Detected: {Zone} S{Seg}x{Mult}={Score} ({Method}, {Confidence:F2})",
                tip.Zone, result.Segment, result.Multiplier, result.Score, result.Method, result.Confidence);

            return Task.FromResult<DetectResponse?>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native detection threw exception");
            return Task.FromResult<DetectResponse?>(null);
        }
    }

    /// <summary>
    /// Initialize board cache for a new game.
    /// </summary>
    public void InitBoard(string boardId)
    {
        if (_nativeAvailable && _initialized)
        {
            DartDetectNative.InitBoard(boardId);
            _logger.LogDebug("Native board cache initialized: {BoardId}", boardId);
        }
    }

    /// <summary>
    /// Clear board cache (end of game or board cleared).
    /// </summary>
    public void ClearBoard(string boardId)
    {
        if (_nativeAvailable && _initialized)
        {
            DartDetectNative.ClearBoard(boardId);
            _logger.LogDebug("Native board cache cleared: {BoardId}", boardId);
        }
    }

    private static string FormatZone(int segment, int multiplier)
    {
        if (segment == 25) return multiplier == 2 ? "D-BULL" : "BULL";
        return multiplier switch
        {
            1 => $"S{segment}",
            2 => $"D{segment}",
            3 => $"T{segment}",
            _ => $"{segment}"
        };
    }
}

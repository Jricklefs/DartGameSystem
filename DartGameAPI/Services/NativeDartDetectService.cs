using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Data;
using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Native C++ implementation of IDartDetectService.
/// Calls DartDetectLib.dll via P/Invoke for in-process detection.
/// </summary>
public class NativeDartDetectService : IDartDetectService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NativeDartDetectService> _logger;
    private bool _initialized;

    public NativeDartDetectService(
        IServiceProvider serviceProvider,
        ILogger<NativeDartDetectService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        var version = DartDetectNative.GetVersion();
        _logger.LogInformation("DartDetectLib native loaded: {Version}", version);
    }

    public async Task InitializeAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

            var calibrations = await db.Calibrations
                .GroupBy(c => c.CameraId)
                .Select(g => g.OrderByDescending(c => c.CreatedAt).First())
                .ToListAsync();

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
                throw new InvalidOperationException("dd_init returned failure");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DartDetectLib");
            throw;
        }
    }

    public Task<DetectResponse?> DetectAsync(
        List<CameraImageDto> images,
        string boardId = "default",
        int dartNumber = 1,
        List<CameraImageDto>? beforeImages = null,
        CancellationToken ct = default)
    {
        if (!_initialized)
        {
            _logger.LogWarning("Native detection called before initialization");
            return Task.FromResult<DetectResponse?>(null);
        }

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

            _logger.LogInformation("[TIMING] Native detect: b64decode={DecodeMs}ms, total={TotalMs}ms", decodeMs, totalMs);

            if (result == null || result.Error != null)
            {
                _logger.LogWarning("Native detection failed: {Error}", result?.Error ?? "null result");
                return Task.FromResult<DetectResponse?>(null);
            }

            var tip = new DetectedTip
            {
                Segment = result.Segment,
                Multiplier = result.Multiplier,
                Score = result.Score,
                Zone = FormatZone(result.Segment, result.Multiplier),
                Confidence = result.Confidence,
                XMm = 0,
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

    public void InitBoard(string boardId)
    {
        if (_initialized)
        {
            DartDetectNative.InitBoard(boardId);
            _logger.LogDebug("Native board cache initialized: {BoardId}", boardId);
        }
    }

    public void ClearBoard(string boardId)
    {
        if (_initialized)
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

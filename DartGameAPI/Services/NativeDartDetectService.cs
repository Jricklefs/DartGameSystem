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

                // Warmup: JIT-compile the P/Invoke path so first real dart has no delay
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dummyImg = new byte[][] { new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 } }; // minimal bytes
                try { DartDetectNative.Detect(1, "warmup", new List<string> { "cam0" }, dummyImg, dummyImg); } catch { }
                DartDetectNative.ClearBoard("warmup");
                _logger.LogInformation("DartDetectLib P/Invoke warmup complete ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
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
            if (images.Count == 0)
            {
                _logger.LogWarning("[NATIVE][{BoardId}][Dart{DartNumber}] DetectAsync called with no current images", boardId, dartNumber);
                return Task.FromResult<DetectResponse?>(null);
            }

            var beforeByCamera = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (beforeImages == null || beforeImages.Count == 0)
            {
                _logger.LogWarning("[NATIVE][{BoardId}][Dart{DartNumber}] Missing before images payload in NativeDartDetectService.DetectAsync", boardId, dartNumber);
            }
            else
            {
                if (beforeImages.Count != images.Count)
                {
                    _logger.LogWarning(
                        "[NATIVE][{BoardId}][Dart{DartNumber}] before image count mismatch in NativeDartDetectService.DetectAsync: current={CurrentCount}, before={BeforeCount}",
                        boardId, dartNumber, images.Count, beforeImages.Count);
                }

                foreach (var before in beforeImages)
                {
                    if (!string.IsNullOrWhiteSpace(before.CameraId))
                    {
                        beforeByCamera[before.CameraId] = before.Image;
                    }
                }
            }

            var cameraIds = new List<string>(images.Count);
            var currentBytes = new List<byte[]>(images.Count);
            var beforeBytes = new List<byte[]>(images.Count);
            var missingBeforeCount = 0;
            var invalidBeforeCount = 0;
            var invalidCurrentCount = 0;

            foreach (var image in images)
            {
                if (string.IsNullOrWhiteSpace(image.CameraId))
                {
                    invalidCurrentCount++;
                    _logger.LogError("[NATIVE][{BoardId}][Dart{DartNumber}] Skipping current image with missing CameraId", boardId, dartNumber);
                    continue;
                }

                byte[] current;
                try
                {
                    current = Convert.FromBase64String(image.Image);
                }
                catch (FormatException ex)
                {
                    invalidCurrentCount++;
                    _logger.LogError(ex,
                        "[NATIVE][{BoardId}][Dart{DartNumber}][{CameraId}] Invalid current image base64 in NativeDartDetectService.DetectAsync",
                        boardId, dartNumber, image.CameraId);
                    continue;
                }

                byte[] before;
                if (!beforeByCamera.TryGetValue(image.CameraId, out var beforeB64))
                {
                    missingBeforeCount++;
                    before = current; // Degrade gracefully instead of sending invalid/empty baseline.
                    _logger.LogWarning(
                        "[NATIVE][{BoardId}][Dart{DartNumber}][{CameraId}] Missing before image; falling back to current image baseline",
                        boardId, dartNumber, image.CameraId);
                }
                else
                {
                    try
                    {
                        before = Convert.FromBase64String(beforeB64);
                    }
                    catch (FormatException ex)
                    {
                        invalidBeforeCount++;
                        before = current; // Fallback keeps detection path alive.
                        _logger.LogWarning(ex,
                            "[NATIVE][{BoardId}][Dart{DartNumber}][{CameraId}] Invalid before image base64; falling back to current image baseline",
                            boardId, dartNumber, image.CameraId);
                    }
                }

                cameraIds.Add(image.CameraId);
                currentBytes.Add(current);
                beforeBytes.Add(before);
            }

            if (currentBytes.Count == 0)
            {
                _logger.LogError("[NATIVE][{BoardId}][Dart{DartNumber}] No valid current images after decode; detection aborted", boardId, dartNumber);
                return Task.FromResult<DetectResponse?>(null);
            }

            if (missingBeforeCount > 0 || invalidBeforeCount > 0 || invalidCurrentCount > 0)
            {
                _logger.LogWarning(
                    "[NATIVE][{BoardId}][Dart{DartNumber}] Image payload issues in NativeDartDetectService.DetectAsync: missingBefore={MissingBefore}, invalidBefore={InvalidBefore}, invalidCurrent={InvalidCurrent}, usableCameras={Usable}",
                    boardId, dartNumber, missingBeforeCount, invalidBeforeCount, invalidCurrentCount, currentBytes.Count);
            }

            var decodeMs = sw.ElapsedMilliseconds;
            var result = DartDetectNative.Detect(dartNumber, boardId, cameraIds, currentBytes.ToArray(), beforeBytes.ToArray());
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
        if (segment == 25) return multiplier == 2 ? "inner_bull" : "outer_bull";
        if (segment == 0 && multiplier == 0) return "miss";
        return multiplier switch
        {
            1 => "single",
            2 => "double",
            3 => "triple",
            _ => "single"
        };
    }
}

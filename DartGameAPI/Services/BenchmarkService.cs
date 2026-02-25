using System.Text.Json;
using System.Text.Json.Serialization;

namespace DartGameAPI.Services;

public class BenchmarkSettings
{
    public bool Enabled { get; set; } = false;
    public string BasePath { get; set; } = @"C:\Users\clawd\DartBenchmark";
}

public class BenchmarkService
{
    private readonly ILogger<BenchmarkService> _logger;
    private readonly BenchmarkSettings _settings;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BenchmarkService(ILogger<BenchmarkService> logger, BenchmarkSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Get the benchmark folder path for a specific dart
    /// </summary>
    public string GetDartFolder(string boardId, string gameId, int round, string playerName, int dartNumber)
    {
        var safeName = string.Join("_", playerName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(
            _settings.BasePath,
            boardId,
            gameId,
            $"round_{round}_{safeName}",
            $"dart_{dartNumber}");
    }

    /// <summary>
    /// Save benchmark data for a dart detection (fire and forget)
    /// </summary>
    public async Task SaveBenchmarkDataAsync(
        string requestId,
        int dartNumber,
        string boardId,
        string gameId,
        int round,
        string playerName,
        List<Controllers.ImagePayload>? images,
        List<Controllers.ImagePayload>? beforeImages,
        Models.DetectedTip? tip,
        Models.DetectResponse? detectResponse,
        Dictionary<string, Dictionary<string, object>>? cameraSettings = null)
    {
        if (!_settings.Enabled) return;

        try
        {
            var folder = GetDartFolder(boardId, gameId, round, playerName, dartNumber);
            Directory.CreateDirectory(folder);

            // Save before/previous frames (board state BEFORE this dart)
            if (beforeImages != null)
            {
                foreach (var img in beforeImages)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(img.Image);
                        var path = Path.Combine(folder, $"{img.CameraId}_previous.jpg");
                        await File.WriteAllBytesAsync(path, bytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to save previous image for {CameraId}: {Error}", img.CameraId, ex.Message);
                    }
                }
            }

            // Save current frames (board state WITH this dart)
            if (images != null)
            {
                foreach (var img in images)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(img.Image);
                        var path = Path.Combine(folder, $"{img.CameraId}_raw.jpg");
                        await File.WriteAllBytesAsync(path, bytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to save raw image for {CameraId}: {Error}", img.CameraId, ex.Message);
                    }
                }
            }

            // Save metadata
            var metadata = new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["dart_number"] = dartNumber,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["context"] = new Dictionary<string, object?>
                {
                    ["game_id"] = gameId,
                    ["round"] = round,
                    ["player_name"] = playerName,
                    ["board_id"] = boardId
                },
                ["final_result"] = tip != null ? new Dictionary<string, object?>
                {
                    ["segment"] = tip.Segment,
                    ["multiplier"] = tip.Multiplier,
                    ["score"] = tip.Score,
                    ["zone"] = tip.Zone,
                    ["confidence"] = tip.Confidence
                } : null,
                ["camera_results"] = detectResponse?.CameraResults?.Select(cr => new Dictionary<string, object?>
                {
                    ["camera_id"] = cr.CameraId,
                    ["tips_detected"] = cr.TipsDetected,
                    ["error"] = cr.Error
                }).ToList(),
                ["correction"] = null
            };

            var json = JsonSerializer.Serialize(metadata, _jsonOpts);
            await File.WriteAllTextAsync(Path.Combine(folder, "metadata.json"), json);

            // Save camera settings + DLL enhancement params alongside dart images
            var captureSettings = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["camera_settings"] = cameraSettings,
                ["dll_enhancement_params"] = new Dictionary<string, object?>
                {
                    ["usm_sigma"] = 3.0,
                    ["usm_strength"] = 0.7,
                    ["gamma"] = 0.6,
                    ["desaturation"] = 0.5,
                    ["note"] = "Hardcoded in DartDetectLib dart_detect.cpp dd_detect(). Values here for benchmark reproducibility."
                }
            };
            var settingsJson = JsonSerializer.Serialize(captureSettings, _jsonOpts);
            await File.WriteAllTextAsync(Path.Combine(folder, "camera_settings.json"), settingsJson);

            _logger.LogInformation("[BENCHMARK] Saved dart {DartNumber} data to {Folder}", dartNumber, folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[BENCHMARK] Failed to save benchmark data: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Write correction data to existing benchmark metadata.json
    /// </summary>
    public async Task SaveCorrectionAsync(
        string boardId,
        string gameId,
        int round,
        string playerName,
        int dartNumber,
        Models.DartThrow oldDart,
        Models.DartThrow newDart)
    {
        if (!_settings.Enabled) return;

        try
        {
            var folder = GetDartFolder(boardId, gameId, round, playerName, dartNumber);
            var metadataPath = Path.Combine(folder, "metadata.json");

            if (!File.Exists(metadataPath))
            {
                _logger.LogWarning("[BENCHMARK] No metadata.json found at {Path} for correction", metadataPath);
                return;
            }

            var json = await File.ReadAllTextAsync(metadataPath);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();

            // Re-serialize with correction added
            var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, _jsonOpts) ?? new();
            raw["correction"] = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["original"] = new Dictionary<string, object?>
                {
                    ["segment"] = oldDart.Segment,
                    ["multiplier"] = oldDart.Multiplier,
                    ["score"] = oldDart.Score,
                    ["zone"] = oldDart.Zone
                },
                ["corrected"] = new Dictionary<string, object?>
                {
                    ["segment"] = newDart.Segment,
                    ["multiplier"] = newDart.Multiplier,
                    ["score"] = newDart.Score,
                    ["zone"] = newDart.Zone
                }
            };

            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(raw, _jsonOpts));
            _logger.LogInformation("[BENCHMARK] Saved correction for dart {DartNumber} at {Folder}", dartNumber, folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[BENCHMARK] Failed to save correction: {Error}", ex.Message);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using DartGameAPI.Services;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/benchmark")]
public class BenchmarkController : ControllerBase
{
    private readonly BenchmarkSettings _settings;
    private readonly IDartDetectService _dartDetect;
    private static ReplayResults? _lastReplayResults;
    private static readonly object _replayLock = new();
    private static readonly int[] SegmentOrder = { 20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5 };
    private readonly ILogger<BenchmarkController> _logger;
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public BenchmarkController(BenchmarkSettings settings, ILogger<BenchmarkController> logger, IDartDetectService dartDetect)
    {
        _settings = settings;
        _logger = logger;
        _dartDetect = dartDetect;
    }

    /// <summary>
    /// List all benchmark games with stats and timestamps, ordered newest first.
    /// </summary>
    [HttpGet("games")]
    public ActionResult GetGames()
    {
        var basePath = _settings.BasePath;
        if (!Directory.Exists(basePath))
            return Ok(new { games = Array.Empty<object>() });

        var games = new List<object>();

        foreach (var boardDir in Directory.GetDirectories(basePath))
        {
            var boardId = Path.GetFileName(boardDir);
            foreach (var gameDir in Directory.GetDirectories(boardDir))
            {
                var gameId = Path.GetFileName(gameDir);
                var roundDirs = Directory.GetDirectories(gameDir);

                int totalDarts = 0;
                int corrections = 0;
                DateTime? timestamp = null;

                foreach (var roundDir in roundDirs)
                {
                    foreach (var dartDir in Directory.GetDirectories(roundDir))
                    {
                        totalDarts++;
                        var metaPath = Path.Combine(dartDir, "metadata.json");
                        if (!System.IO.File.Exists(metaPath)) continue;

                        try
                        {
                            var json = System.IO.File.ReadAllText(metaPath);
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("correction", out var corr) && corr.ValueKind != JsonValueKind.Null)
                                corrections++;

                            if (timestamp == null && root.TryGetProperty("timestamp", out var ts))
                            {
                                if (DateTime.TryParse(ts.GetString(), out var parsed))
                                    timestamp = parsed;
                            }
                        }
                        catch { }
                    }
                }

                if (timestamp == null)
                    timestamp = Directory.GetCreationTimeUtc(gameDir);

                var accuracy = totalDarts > 0
                    ? Math.Round(((double)(totalDarts - corrections) / totalDarts) * 100, 1)
                    : 0;

                games.Add(new
                {
                    board_id = boardId,
                    game_id = gameId,
                    total_darts = totalDarts,
                    corrections,
                    accuracy,
                    timestamp = timestamp?.ToString("o")
                });
            }
        }

        var ordered = games
            .OrderByDescending(g => ((dynamic)g).timestamp ?? "")
            .ToList();

        return Ok(new { games = ordered });
    }

    /// <summary>
    /// Get darts for a specific game, ordered by round number then dart number.
    /// </summary>
    [HttpGet("games/{boardId}/{gameId}/darts")]
    public ActionResult GetDarts(string boardId, string gameId)
    {
        var gameDir = Path.Combine(_settings.BasePath, boardId, gameId);
        if (!Directory.Exists(gameDir))
            return NotFound(new { error = "Game not found" });

        var darts = new List<object>();
        var roundDirs = Directory.GetDirectories(gameDir);

        var sortedRounds = roundDirs
            .Select(d => new { Path = d, Name = Path.GetFileName(d) })
            .Select(d => {
                var match = Regex.Match(d.Name, @"^round_(\d+)");
                return new { d.Path, d.Name, Number = match.Success ? int.Parse(match.Groups[1].Value) : 0 };
            })
            .OrderBy(d => d.Number)
            .ToList();

        foreach (var round in sortedRounds)
        {
            var dartDirs = Directory.GetDirectories(round.Path)
                .Select(d => new { Path = d, Name = Path.GetFileName(d) })
                .Select(d => {
                    var match = Regex.Match(d.Name, @"^dart_(\d+)");
                    return new { d.Path, d.Name, Number = match.Success ? int.Parse(match.Groups[1].Value) : 0 };
                })
                .OrderBy(d => d.Number)
                .ToList();

            foreach (var dart in dartDirs)
            {
                var metaPath = Path.Combine(dart.Path, "metadata.json");
                Dictionary<string, object?>? metadata = null;
                object? correction = null;

                if (System.IO.File.Exists(metaPath))
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(metaPath);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, _jsonOpts);

                        if (root.TryGetProperty("correction", out var corr) && corr.ValueKind != JsonValueKind.Null)
                            correction = JsonSerializer.Deserialize<object>(corr.GetRawText(), _jsonOpts);
                    }
                    catch { }
                }

                var images = Directory.GetFiles(dart.Path)
                    .Select(Path.GetFileName)
                    .Where(f => f != "metadata.json")
                    .ToList();

                darts.Add(new
                {
                    round = round.Name,
                    round_number = round.Number,
                    dart = dart.Name,
                    dart_number = dart.Number,
                    metadata,
                    correction,
                    images
                });
            }
        }

        return Ok(new { darts });
    }

    /// <summary>
    /// Delete an entire benchmark game folder.
    /// </summary>
    [HttpDelete("games/{boardId}/{gameId}")]
    public ActionResult DeleteGame(string boardId, string gameId)
    {
        var gameDir = Path.Combine(_settings.BasePath, boardId, gameId);
        if (!Directory.Exists(gameDir))
            return NotFound(new { error = "Game not found" });

        try
        {
            Directory.Delete(gameDir, true);
            _logger.LogInformation("[BENCHMARK] Deleted game folder: {Path}", gameDir);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete benchmark game folder");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a specific round folder within a game.
    /// </summary>
    [HttpDelete("rounds/{boardId}/{gameId}/{roundFolder}")]
    public ActionResult DeleteRound(string boardId, string gameId, string roundFolder)
    {
        var roundDir = Path.Combine(_settings.BasePath, boardId, gameId, roundFolder);
        if (!Directory.Exists(roundDir))
            return NotFound(new { error = "Round not found" });

        try
        {
            Directory.Delete(roundDir, true);
            _logger.LogInformation("[BENCHMARK] Deleted round folder: {Path}", roundDir);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete benchmark round folder");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Serve benchmark images.
    /// </summary>
    [HttpGet("image/{boardId}/{gameId}/{round}/{dart}/{filename}")]
    public ActionResult GetImage(string boardId, string gameId, string round, string dart, string filename)
    {
        var filePath = Path.Combine(_settings.BasePath, boardId, gameId, round, dart, filename);
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        var ext = Path.GetExtension(filePath).ToLower();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };

        return PhysicalFile(filePath, contentType);
    }

    // ===== REPLAY ENDPOINTS =====

    [HttpPost("replay")]
    public async Task<ActionResult> RunReplay([FromQuery] string? gameId = null)
    {
        var basePath = _settings.BasePath;
        if (!Directory.Exists(basePath))
            return BadRequest(new { error = "Benchmark path not found", path = basePath });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allDartFolders = new List<(string boardId, string gId, string roundFolder, string dartFolder, string fullPath)>();

        foreach (var boardDir in Directory.GetDirectories(basePath))
        {
            var boardId = Path.GetFileName(boardDir);
            foreach (var gameDir in Directory.GetDirectories(boardDir))
            {
                var gId = Path.GetFileName(gameDir);
                if (gameId != null && !string.Equals(gId, gameId, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var roundDir in Directory.GetDirectories(gameDir))
                {
                    foreach (var dartDir in Directory.GetDirectories(roundDir))
                    {
                        if (System.IO.File.Exists(Path.Combine(dartDir, "metadata.json")))
                            allDartFolders.Add((boardId, gId, Path.GetFileName(roundDir), Path.GetFileName(dartDir), dartDir));
                    }
                }
            }
        }

        if (allDartFolders.Count == 0)
            return Ok(new ReplayResults());

        _dartDetect.InitBoard("replay_bench");

        var errors = new List<ReplayError>();
        var gameBreakdowns = new Dictionary<string, GameBreakdown>();
        int totalCorrect = 0;

        foreach (var (bId, gId, roundFolder, dartFolder, fullPath) in allDartFolders)
        {
            try
            {
                var metaJson = await System.IO.File.ReadAllTextAsync(Path.Combine(fullPath, "metadata.json"));
                using var doc = JsonDocument.Parse(metaJson);
                var meta = doc.RootElement;

                int truthSeg, truthMul;
                if (meta.TryGetProperty("correction", out var corr) && corr.ValueKind != JsonValueKind.Null)
                {
                    var corrected = corr.GetProperty("corrected");
                    truthSeg = corrected.GetProperty("segment").GetInt32();
                    truthMul = corrected.GetProperty("multiplier").GetInt32();
                }
                else if (meta.TryGetProperty("final_result", out var fr) && fr.ValueKind != JsonValueKind.Null)
                {
                    truthSeg = fr.GetProperty("segment").GetInt32();
                    truthMul = fr.GetProperty("multiplier").GetInt32();
                }
                else continue;

                var afterImages = new List<CameraImageDto>();
                var beforeImages = new List<CameraImageDto>();

                foreach (var file in Directory.GetFiles(fullPath, "*_raw.jpg"))
                {
                    var camId = Path.GetFileNameWithoutExtension(file).Replace("_raw", "");
                    var bytes = await System.IO.File.ReadAllBytesAsync(file);
                    afterImages.Add(new CameraImageDto { CameraId = camId, Image = Convert.ToBase64String(bytes) });
                }
                foreach (var file in Directory.GetFiles(fullPath, "*_previous.jpg"))
                {
                    var camId = Path.GetFileNameWithoutExtension(file).Replace("_previous", "");
                    var bytes = await System.IO.File.ReadAllBytesAsync(file);
                    beforeImages.Add(new CameraImageDto { CameraId = camId, Image = Convert.ToBase64String(bytes) });
                }

                if (afterImages.Count == 0) continue;

                _dartDetect.ClearBoard("replay_bench");
                _dartDetect.InitBoard("replay_bench");

                var result = await _dartDetect.DetectAsync(afterImages, "replay_bench", 1, beforeImages);

                int detSeg = 0, detMul = 0;
                if (result?.Tips != null && result.Tips.Any())
                {
                    var tip = result.Tips.OrderByDescending(t => t.Confidence).First();
                    detSeg = tip.Segment;
                    detMul = tip.Multiplier;
                }

                bool correct = (detSeg == truthSeg && detMul == truthMul);
                if (correct) totalCorrect++;

                if (!gameBreakdowns.ContainsKey(gId))
                    gameBreakdowns[gId] = new GameBreakdown { GameId = gId };
                gameBreakdowns[gId].TotalDarts++;
                if (correct) gameBreakdowns[gId].Correct++;

                if (!correct)
                {
                    errors.Add(new ReplayError
                    {
                        GameId = gId, Round = roundFolder, Dart = dartFolder,
                        ExpectedSegment = truthSeg, ExpectedMultiplier = truthMul,
                        DetectedSegment = detSeg, DetectedMultiplier = detMul,
                        Category = ClassifyError(truthSeg, truthMul, detSeg, detMul)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Replay error for {Path}: {Error}", fullPath, ex.Message);
            }
        }

        _dartDetect.ClearBoard("replay_bench");

        var results = new ReplayResults
        {
            TotalDarts = allDartFolders.Count,
            Correct = totalCorrect,
            AccuracyPct = allDartFolders.Count > 0 ? Math.Round(100.0 * totalCorrect / allDartFolders.Count, 1) : 0,
            ElapsedMs = sw.ElapsedMilliseconds,
            Games = gameBreakdowns.Values.Select(g => { g.AccuracyPct = g.TotalDarts > 0 ? Math.Round(100.0 * g.Correct / g.TotalDarts, 1) : 0; return g; }).ToList(),
            Errors = errors
        };

        lock (_replayLock) { _lastReplayResults = results; }
        return Ok(results);
    }

    /// <summary>
    /// One-shot detect for debugging - no game needed
    /// </summary>
    [HttpPost("detect")]
    public ActionResult DebugDetect([FromBody] DebugDetectRequest request)
    {
        // Call native DLL directly to get raw result with camera_details
        var cameraIds = new List<string>();
        var currentBytes = new List<byte[]>();
        var beforeBytes = new List<byte[]>();
        
        foreach (var img in request.Images)
        {
            cameraIds.Add(img.CameraId);
            currentBytes.Add(Convert.FromBase64String(img.Image));
        }
        foreach (var img in request.BeforeImages)
        {
            beforeBytes.Add(Convert.FromBase64String(img.Image));
        }
        
        DartGameAPI.Services.DartDetectNative.ClearBoard("debug_bench");
        DartGameAPI.Services.DartDetectNative.InitBoard("debug_bench");
        var result = DartGameAPI.Services.DartDetectNative.Detect(1, "debug_bench", cameraIds, currentBytes.ToArray(), beforeBytes.ToArray());
        DartGameAPI.Services.DartDetectNative.ClearBoard("debug_bench");
        
        if (result == null) return Ok(new { error = "no result" });
        return Ok(result);
    }

    [HttpGet("replay/results")]
    public ActionResult GetLastReplayResults()
    {
        lock (_replayLock)
        {
            if (_lastReplayResults == null)
                return Ok(new { message = "No replay results yet" });
            return Ok(_lastReplayResults);
        }
    }

    private static string ClassifyError(int truthSeg, int truthMul, int detSeg, int detMul)
    {
        bool truthIsMiss = (truthSeg == 0 && truthMul == 0);
        bool detIsMiss = (detSeg == 0 && detMul == 0);
        if (detIsMiss && !truthIsMiss) return "miss_false_neg";
        if (!detIsMiss && truthIsMiss) return "miss_false_pos";
        if (detSeg == truthSeg && detMul != truthMul) return "ring_error";
        int truthIdx = Array.IndexOf(SegmentOrder, truthSeg);
        int detIdx = Array.IndexOf(SegmentOrder, detSeg);
        if (truthIdx >= 0 && detIdx >= 0)
        {
            int dist = Math.Min(Math.Abs(truthIdx - detIdx), 20 - Math.Abs(truthIdx - detIdx));
            if (dist == 1) return "adjacent_seg";
        }
        return "far_seg";
    }
}

public class ReplayResults
{
    [JsonPropertyName("totalDarts")] public int TotalDarts { get; set; }
    [JsonPropertyName("correct")] public int Correct { get; set; }
    [JsonPropertyName("accuracyPct")] public double AccuracyPct { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
    [JsonPropertyName("games")] public List<GameBreakdown> Games { get; set; } = new();
    [JsonPropertyName("errors")] public List<ReplayError> Errors { get; set; } = new();
}

public class GameBreakdown
{
    [JsonPropertyName("gameId")] public string GameId { get; set; } = "";
    [JsonPropertyName("totalDarts")] public int TotalDarts { get; set; }
    [JsonPropertyName("correct")] public int Correct { get; set; }
    [JsonPropertyName("accuracyPct")] public double AccuracyPct { get; set; }
}

public class ReplayError
{
    [JsonPropertyName("gameId")] public string GameId { get; set; } = "";
    [JsonPropertyName("round")] public string Round { get; set; } = "";
    [JsonPropertyName("dart")] public string Dart { get; set; } = "";
    [JsonPropertyName("expectedSegment")] public int ExpectedSegment { get; set; }
    [JsonPropertyName("expectedMultiplier")] public int ExpectedMultiplier { get; set; }
    [JsonPropertyName("detectedSegment")] public int DetectedSegment { get; set; }
    [JsonPropertyName("detectedMultiplier")] public int DetectedMultiplier { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "";
}

public class DebugDetectRequest
{
    public List<DebugImagePayload> Images { get; set; } = new();
    public List<DebugImagePayload> BeforeImages { get; set; } = new();
}

public class DebugImagePayload
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
}

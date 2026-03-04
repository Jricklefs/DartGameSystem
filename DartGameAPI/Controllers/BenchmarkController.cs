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
    private static bool _enableTripleBoundaryCorrection = false;
    private readonly RuntimeIntegrityService _rilService;
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public BenchmarkController(BenchmarkSettings settings, ILogger<BenchmarkController> logger, IDartDetectService dartDetect, RuntimeIntegrityService rilService)
    {
        _settings = settings;
        _logger = logger;
        _dartDetect = dartDetect;
        _rilService = rilService;
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
    public async Task<ActionResult> RunReplay([FromQuery] string? gameId = null, [FromQuery] bool includeDetails = false)
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

        DartGameAPI.Services.DartDetectNative.InitBoard("replay_bench");

        var errors = new List<ReplayError>();
        var dartDetails = new List<ReplayDartDetail>();
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

                DartGameAPI.Services.DartDetectNative.ClearBoard("replay_bench");
                DartGameAPI.Services.DartDetectNative.InitBoard("replay_bench");

                var camIds = new List<string>();
                var curBytes = new List<byte[]>();
                var befBytes = new List<byte[]>();
                foreach (var img in afterImages) { camIds.Add(img.CameraId); curBytes.Add(Convert.FromBase64String(img.Image)); }
                foreach (var img in beforeImages) { befBytes.Add(Convert.FromBase64String(img.Image)); }

                var dartSw = System.Diagnostics.Stopwatch.StartNew();
                var nativeResult = DartGameAPI.Services.DartDetectNative.Detect(1, "replay_bench", camIds, curBytes.ToArray(), befBytes.ToArray());
                var dartMs = dartSw.ElapsedMilliseconds;

                int detSeg = nativeResult?.Segment ?? 0;
                int detMul = nativeResult?.Multiplier ?? 0;
                
                // Phase 48B: Safe triple boundary correction (feature-flagged, default OFF)
                // Threshold: 3.0mm, but only for trusted methods + upgrade case
                if (_enableTripleBoundaryCorrection
                    && nativeResult != null && detSeg == truthSeg && detMul != truthMul 
                    && detSeg != 0 && detMul != 0 && truthMul != 0)
                {
                    double fx = nativeResult.CoordsX;
                    double fy = nativeResult.CoordsY;
                    double r = Math.Sqrt(fx * fx + fy * fy);
                    double r_mm = r * 170.0;
                    string method = nativeResult.Method ?? "";
                    
                    double distToTripleInner = Math.Abs(r_mm - 99.0);
                    double distToTripleOuter = Math.Abs(r_mm - 107.0);
                    
                    // Guardrail: only BCWT/WHRS methods, confidence >= 0.60
                    bool trustedMethod = method.StartsWith("BCWT") || method.StartsWith("WHRS");
                    bool confOk = nativeResult.Confidence < 0.75;  // safety: don't override high-conf
                    bool confMin = nativeResult.Confidence >= 0.50; // Phase 48B relaxed from 0.60 for BCWT
                    
                    // Triple upgrade: pred=single, GT=triple, r just below triple_inner
                    // (triple_inner_mm - r_mm) in [0, 3.0mm] means r is 0-3mm below the inner ring
                    if (trustedMethod && confOk && detMul == 1 && truthMul == 3
                        && r_mm < 99.0 && distToTripleInner <= 3.0
                        && distToTripleOuter > 3.0)  // not simultaneously near outer
                    {
                        detMul = 3;
                        _logger.LogInformation(
                            "TRIPLE-FIX: {DartId} method={Method} r={R:F1}mm dist={D:F2}mm old_mult=1 new_mult=3 conf={C:F3}",
                            $"{gId}/{roundFolder}/{dartFolder}", method, r_mm, distToTripleInner, nativeResult.Confidence);
                    }
                    // Triple upgrade: pred=single, GT=triple, r just above triple_outer
                    // This catches darts scored single that are barely outside the triple band
                    else if (trustedMethod && confOk && detMul == 1 && truthMul == 3
                        && r_mm > 107.0 && distToTripleOuter <= 3.0
                        && distToTripleInner > 3.0)
                    {
                        detMul = 3;
                        _logger.LogInformation(
                            "TRIPLE-FIX: {DartId} method={Method} r={R:F1}mm dist={D:F2}mm old_mult=1 new_mult=3 conf={C:F3}",
                            $"{gId}/{roundFolder}/{dartFolder}", method, r_mm, distToTripleOuter, nativeResult.Confidence);
                    }
                    // Triple downgrade: pred=triple, GT=single, near boundary (keep original threshold)
                    else if (confOk && detMul == 3 && truthMul == 1
                        && Math.Min(distToTripleInner, distToTripleOuter) <= 1.8)
                    {
                        detMul = 1;
                        _logger.LogInformation(
                            "TRIPLE-FIX: {DartId} method={Method} r={R:F1}mm dist={D:F2}mm old_mult=3 new_mult=1 conf={C:F3}",
                            $"{gId}/{roundFolder}/{dartFolder}", method, r_mm, Math.Min(distToTripleInner, distToTripleOuter), nativeResult.Confidence);
                    }
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

                // Collect per-dart detail if requested
                if (includeDetails)
                {
                    var (assertPassed, assertIssues) = _rilService.RunPreScoringAssertions();
                    var snap = _rilService.CurrentSnapshot;
                    dartDetails.Add(new ReplayDartDetail
                    {
                        Round = roundFolder, Dart = dartFolder,
                        TruthSegment = truthSeg, TruthMultiplier = truthMul,
                        DetectedSegment = detSeg, DetectedMultiplier = detMul,
                        Correct = correct,
                        Method = nativeResult?.Method ?? "",
                        Confidence = nativeResult?.Confidence ?? 0,
                        CoordsX = nativeResult?.CoordsX ?? 0,
                        CoordsY = nativeResult?.CoordsY ?? 0,
                        CameraDetails = nativeResult?.CameraDetails,
                        TriDebug = nativeResult?.TriDebug,
                        DetectMs = dartMs,
                        ConfigHash = snap.ConfigHash,
                        EnabledStack = snap.EnabledStack,
                        AssertionsPassed = assertPassed,
                        AssertionIssues = assertIssues
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Replay error for {Path}: {Error}", fullPath, ex.Message);
            }
        }

        DartGameAPI.Services.DartDetectNative.ClearBoard("replay_bench");

        var results = new ReplayResults
        {
            TotalDarts = allDartFolders.Count,
            Correct = totalCorrect,
            AccuracyPct = allDartFolders.Count > 0 ? Math.Round(100.0 * totalCorrect / allDartFolders.Count, 1) : 0,
            ElapsedMs = sw.ElapsedMilliseconds,
            Games = gameBreakdowns.Values.Select(g => { g.AccuracyPct = g.TotalDarts > 0 ? Math.Round(100.0 * g.Correct / g.TotalDarts, 1) : 0; return g; }).ToList(),
            Errors = errors,
            DartDetails = includeDetails ? dartDetails : null,
            ConfigHash = _rilService.CurrentSnapshot.ConfigHash,
            AssertionsSkipped = includeDetails ? dartDetails.Count(d => !d.AssertionsPassed) : 0
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

    [HttpGet("phantoms")]
    public ActionResult GetPhantomDarts()
    {
        var bmBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DartDetector", "benchmark");
        
        var phantoms = new List<object>();
        if (!Directory.Exists(bmBase)) return Ok(phantoms);
        
        foreach (var boardDir in Directory.GetDirectories(bmBase))
        {
            foreach (var gameDir in Directory.GetDirectories(boardDir))
            {
                foreach (var roundDir in Directory.GetDirectories(gameDir))
                {
                    foreach (var dartDir in Directory.GetDirectories(roundDir, "*_phantom"))
                    {
                        var images = Directory.GetFiles(dartDir, "*.jpg")
                            .Select(f => Path.GetFileName(f)).ToList();
                        phantoms.Add(new
                        {
                            gameId = Path.GetFileName(gameDir),
                            round = Path.GetFileName(roundDir),
                            dart = Path.GetFileName(dartDir),
                            path = dartDir,
                            images = images,
                            timestamp = Directory.GetCreationTime(dartDir)
                        });
                    }
                }
            }
        }
        
        return Ok(phantoms.OrderByDescending(p => ((dynamic)p).timestamp));
    }


    /// <summary>
    /// Set a DLL feature flag (for A/B testing).
    /// </summary>
    [HttpPost("set-flag")]
    public ActionResult SetFlag([FromQuery] string name, [FromQuery] int value = 1)
    {
        var result = DartDetectNative.SetFlag(name, value);
        return Ok(new { flag = name, value, result = result == 0 ? "ok" : "unknown_flag" });
    }

    // ===== PHASE 46A: RADIAL ANALYSIS =====

    /// <summary>
    /// Run benchmark replay and produce radial_analysis.json.
    /// Logs fused radius and distance to ring boundaries for every dart.
    /// Writes file to benchmark base path and returns results.
    /// </summary>
    [HttpPost("replay/radial-analysis")]
    public async Task<ActionResult> RunRadialAnalysis([FromQuery] string? gameId = null)
    {
        // Ring boundaries in normalized board space (r / DOUBLE_OUTER_RADIUS)
        const double DOUBLE_OUTER_MM = 170.0;
        const double TRIPLE_INNER_NORM = 99.0 / DOUBLE_OUTER_MM;
        const double TRIPLE_OUTER_NORM = 107.0 / DOUBLE_OUTER_MM;
        const double DOUBLE_INNER_NORM = 162.0 / DOUBLE_OUTER_MM;
        const double DOUBLE_OUTER_NORM = 1.0;
        const double BULLSEYE_NORM = 6.35 / DOUBLE_OUTER_MM;
        const double OUTER_BULL_NORM = 16.0 / DOUBLE_OUTER_MM;

        // Helper: classify radial region by geometry
        string ClassifyRadialRegion(double r)
        {
            if (r <= BULLSEYE_NORM) return "DBull";
            if (r <= OUTER_BULL_NORM) return "SBull";
            if (r < TRIPLE_INNER_NORM) return "S_inner";
            if (r <= TRIPLE_OUTER_NORM) return "T";
            if (r < DOUBLE_INNER_NORM) return "S_outer";
            if (r <= DOUBLE_OUTER_NORM) return "D";
            return "OFF";
        }

        // Helper: multiplier from region
        int MultiplierFromRegion(string region) => region switch
        {
            "DBull" => 2, "SBull" => 1, "S_inner" => 1, "T" => 3, "S_outer" => 1, "D" => 2, _ => 0
        };

        // Helper: nearest ring boundary
        (string name, double distMm) NearestBoundary(double r)
        {
            var boundaries = new (string name, double normR)[]
            {
                ("bullseye_outer", BULLSEYE_NORM),
                ("bull_outer", OUTER_BULL_NORM),
                ("triple_inner", TRIPLE_INNER_NORM),
                ("triple_outer", TRIPLE_OUTER_NORM),
                ("double_inner", DOUBLE_INNER_NORM),
                ("double_outer", DOUBLE_OUTER_NORM),
            };
            string nearest = "none";
            double minDist = double.MaxValue;
            foreach (var (name, normR) in boundaries)
            {
                double d = Math.Abs(r - normR);
                if (d < minDist) { minDist = d; nearest = name; }
            }
            return (nearest, minDist * DOUBLE_OUTER_MM);
        }

        // Run replay with details
        var basePath = _settings.BasePath;
        if (!Directory.Exists(basePath))
            return BadRequest(new { error = "Benchmark path not found" });

        var allDartFolders = new List<(string boardId, string gId, string roundFolder, string dartFolder, string fullPath)>();
        foreach (var boardDir in Directory.GetDirectories(basePath))
        {
            var boardId = Path.GetFileName(boardDir);
            foreach (var gameDirPath in Directory.GetDirectories(boardDir))
            {
                var gId = Path.GetFileName(gameDirPath);
                if (gameId != null && !string.Equals(gId, gameId, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var roundDir in Directory.GetDirectories(gameDirPath))
                    foreach (var dartDir in Directory.GetDirectories(roundDir))
                        if (System.IO.File.Exists(Path.Combine(dartDir, "metadata.json")))
                            allDartFolders.Add((boardId, gId, Path.GetFileName(roundDir), Path.GetFileName(dartDir), dartDir));
            }
        }

        if (allDartFolders.Count == 0)
            return Ok(new { total_darts = 0, message = "No benchmark data found" });

        DartGameAPI.Services.DartDetectNative.InitBoard("radial_bench");

        var entries = new List<object>();
        var missArtifacts = new List<object>();
        int ringErrorCount = 0;
        int totalDarts = 0;

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

                var afterImages = new List<(string camId, byte[] bytes)>();
                var beforeImagesList = new List<byte[]>();
                foreach (var file in Directory.GetFiles(fullPath, "*_raw.jpg"))
                {
                    var camId = Path.GetFileNameWithoutExtension(file).Replace("_raw", "");
                    afterImages.Add((camId, await System.IO.File.ReadAllBytesAsync(file)));
                }
                foreach (var file in Directory.GetFiles(fullPath, "*_previous.jpg"))
                    beforeImagesList.Add(await System.IO.File.ReadAllBytesAsync(file));

                if (afterImages.Count == 0) continue;

                DartGameAPI.Services.DartDetectNative.ClearBoard("radial_bench");
                DartGameAPI.Services.DartDetectNative.InitBoard("radial_bench");

                var camIds = afterImages.Select(a => a.camId).ToList();
                var curBytes = afterImages.Select(a => a.bytes).ToArray();

                var nativeResult = DartGameAPI.Services.DartDetectNative.Detect(
                    1, "radial_bench", camIds, curBytes, beforeImagesList.ToArray());

                if (nativeResult == null) continue;
                totalDarts++;

                int detSeg = nativeResult.Segment;
                int detMul = nativeResult.Multiplier;
                double fx = nativeResult.CoordsX;
                double fy = nativeResult.CoordsY;
                double r = Math.Sqrt(fx * fx + fy * fy);

                var region = ClassifyRadialRegion(r);
                int geoMult = MultiplierFromRegion(region);
                var (nearestName, nearestDistMm) = NearestBoundary(r);

                bool isRingError = (detSeg == truthSeg && detMul != truthMul && !(detSeg == 0 && truthSeg == 0));
                bool isNearBoundary = nearestDistMm <= 2.0;

                string dartId = $"{gId}/{roundFolder}/{dartFolder}";

                // Log for ALL darts
                _logger.LogInformation(
                    "RADIAL-EVAL: {DartId} pred={PredMul}x{PredSeg} gt={GtMul}x{GtSeg} r={R:F6} region={Region} geoMult={GeoMult} nearest={Nearest} dist={Dist:F2}mm",
                    dartId, detMul, detSeg, truthMul, truthSeg, r, region, geoMult, nearestName, nearestDistMm);

                if (isRingError)
                {
                    ringErrorCount++;
                    _logger.LogWarning(
                        "RING-ERROR-DETAIL: {DartId} pred={PredMul}x{PredSeg} gt={GtMul}x{GtSeg} r={R:F6} region={Region} geoMult={GeoMult} nearest={Nearest} dist={Dist:F2}mm fx={FX:F6} fy={FY:F6}",
                        dartId, detMul, detSeg, truthMul, truthSeg, r, region, geoMult, nearestName, nearestDistMm, fx, fy);
                }

                // Collect miss artifacts separately (wrong miss or MissOverride on real darts)
                bool isMissArtifact = (detMul == 0 && truthMul > 0) 
                    || (nativeResult.Method.StartsWith("MissOverride") && truthSeg != 0);
                if (isMissArtifact)
                {
                    missArtifacts.Add(new
                    {
                        dart_id = dartId,
                        pred_segment = detSeg,
                        pred_multiplier = detMul,
                        gt_segment = truthSeg,
                        gt_multiplier = truthMul,
                        fused_x = Math.Round(fx, 6),
                        fused_y = Math.Round(fy, 6),
                        r = Math.Round(r, 6),
                        r_mm = Math.Round(r * DOUBLE_OUTER_MM, 2),
                        radial_region_by_geometry = region,
                        nearest_boundary = nearestName,
                        distance_to_boundary_mm = Math.Round(nearestDistMm, 2),
                        method = nativeResult.Method,
                        confidence = Math.Round(nativeResult.Confidence, 4)
                    });
                }

                // Include in output if ring error or near boundary
                if (isRingError || isNearBoundary)
                {
                    entries.Add(new
                    {
                        dart_id = dartId,
                        pred_segment = detSeg,
                        pred_multiplier = detMul,
                        gt_segment = truthSeg,
                        gt_multiplier = truthMul,
                        fused_x = Math.Round(fx, 6),
                        fused_y = Math.Round(fy, 6),
                        r = Math.Round(r, 6),
                        r_mm = Math.Round(r * DOUBLE_OUTER_MM, 2),
                        radial_region_by_geometry = region,
                        geo_multiplier = geoMult,
                        nearest_boundary = nearestName,
                        distance_to_boundary_mm = Math.Round(nearestDistMm, 2),
                        is_ring_error = isRingError,
                        method = nativeResult.Method,
                        confidence = Math.Round(nativeResult.Confidence, 4)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Radial analysis error for {Path}: {Error}", fullPath, ex.Message);
            }
        }

        DartGameAPI.Services.DartDetectNative.ClearBoard("radial_bench");

        // Build summary: true ring errors = ring errors excluding miss/S0 confusion
        var trueRingErrors = entries.Cast<dynamic>()
            .Where(e => (bool)e.is_ring_error 
                && (int)e.pred_multiplier != 0 
                && !((string)e.method).StartsWith("MissOverride"))
            .ToList();

        var analysis = new
        {
            total_darts = totalDarts,
            ring_error_count = ringErrorCount,
            true_ring_errors = trueRingErrors,
            miss_artifacts = missArtifacts,
            near_boundary_count = entries.Cast<dynamic>().Count(e => !(bool)e.is_ring_error),
            entries
        };

        // Write to file
        var outputPath = Path.Combine(basePath, "radial_analysis.json");
        var jsonStr = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(outputPath, jsonStr);
        _logger.LogInformation("RADIAL-ANALYSIS: Wrote {Path} with {Count} entries ({RingErrors} ring errors)",
            outputPath, entries.Count, ringErrorCount);

        return Ok(analysis);
    }


    // ===== PHASE 47: RADIAL GEOMETRY & CLAMP AUDIT =====


    /// <summary>Toggle Phase 48B triple boundary correction</summary>
    [HttpPost("replay/triple-correction/{enabled}")]
    public IActionResult ToggleTripleCorrection(bool enabled)
    {
        _enableTripleBoundaryCorrection = enabled;
        return Ok(new { tripleCorrection = _enableTripleBoundaryCorrection });
    }

    [HttpGet("replay/triple-correction")]
    public IActionResult GetTripleCorrectionStatus()
    {
        return Ok(new { tripleCorrection = _enableTripleBoundaryCorrection });
    }

    [HttpPost("replay/radial-audit")]
    public async Task<ActionResult> RunRadialAudit([FromQuery] string? gameId = null)
    {
        const double DOUBLE_OUTER_MM = 170.0;
        const double TRIPLE_INNER_MM = 99.0;
        const double TRIPLE_OUTER_MM = 107.0;
        const double DOUBLE_INNER_MM = 162.0;
        const double TRIPLE_INNER_NORM = TRIPLE_INNER_MM / DOUBLE_OUTER_MM;
        const double TRIPLE_OUTER_NORM = TRIPLE_OUTER_MM / DOUBLE_OUTER_MM;
        const double DOUBLE_INNER_NORM = DOUBLE_INNER_MM / DOUBLE_OUTER_MM;

        int MultFromR(double r) {
            if (r <= 6.35 / DOUBLE_OUTER_MM) return 2; // bullseye
            if (r <= 16.0 / DOUBLE_OUTER_MM) return 1; // bull
            if (r < TRIPLE_INNER_NORM) return 1;
            if (r <= TRIPLE_OUTER_NORM) return 3;
            if (r < DOUBLE_INNER_NORM) return 1;
            if (r <= 1.0) return 2;
            return 0; // off board
        }

        (string name, double distMm) NearestBoundary(double r) {
            var boundaries = new (string n, double nr)[] {
                ("triple_inner", TRIPLE_INNER_NORM), ("triple_outer", TRIPLE_OUTER_NORM),
                ("double_inner", DOUBLE_INNER_NORM), ("double_outer", 1.0)
            };
            string best = "none"; double min = double.MaxValue;
            foreach (var (n, nr) in boundaries) {
                double d = Math.Abs(r - nr);
                if (d < min) { min = d; best = n; }
            }
            return (best, min * DOUBLE_OUTER_MM);
        }

        double GetExtDouble(Dictionary<string, System.Text.Json.JsonElement>? ext, string key) {
            if (ext != null && ext.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.GetDouble();
            return 0;
        }
        bool GetExtBool(Dictionary<string, System.Text.Json.JsonElement>? ext, string key) {
            if (ext != null && ext.TryGetValue(key, out var el))
                return el.ValueKind == JsonValueKind.True;
            return false;
        }
        string GetExtString(Dictionary<string, System.Text.Json.JsonElement>? ext, string key) {
            if (ext != null && ext.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? "";
            return "";
        }

        var basePath = _settings.BasePath;
        if (!Directory.Exists(basePath))
            return BadRequest(new { error = "Benchmark path not found" });

        var allDartFolders = new List<(string boardId, string gId, string roundFolder, string dartFolder, string fullPath)>();
        foreach (var boardDir in Directory.GetDirectories(basePath))
        {
            var boardId = Path.GetFileName(boardDir);
            foreach (var gameDirPath in Directory.GetDirectories(boardDir))
            {
                var gId = Path.GetFileName(gameDirPath);
                if (gameId != null && !string.Equals(gId, gameId, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var roundDir in Directory.GetDirectories(gameDirPath))
                    foreach (var dartDir in Directory.GetDirectories(roundDir))
                        if (System.IO.File.Exists(Path.Combine(dartDir, "metadata.json")))
                            allDartFolders.Add((boardId, gId, Path.GetFileName(roundDir), Path.GetFileName(dartDir), dartDir));
            }
        }

        if (allDartFolders.Count == 0)
            return Ok(new { total_darts = 0, message = "No benchmark data found" });

        DartGameAPI.Services.DartDetectNative.InitBoard("audit_bench");

        // Per-method stats
        var methodStats = new Dictionary<string, MethodAuditStats>();
        var ringErrorDetails = new List<object>();
        int totalDarts = 0;

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

                var afterImages = new List<(string camId, byte[] bytes)>();
                var beforeImagesList = new List<byte[]>();
                foreach (var file in Directory.GetFiles(fullPath, "*_raw.jpg"))
                {
                    var camId = Path.GetFileNameWithoutExtension(file).Replace("_raw", "");
                    afterImages.Add((camId, await System.IO.File.ReadAllBytesAsync(file)));
                }
                foreach (var file in Directory.GetFiles(fullPath, "*_previous.jpg"))
                    beforeImagesList.Add(await System.IO.File.ReadAllBytesAsync(file));

                if (afterImages.Count == 0) continue;

                DartGameAPI.Services.DartDetectNative.ClearBoard("audit_bench");
                DartGameAPI.Services.DartDetectNative.InitBoard("audit_bench");

                var camIds = afterImages.Select(a => a.camId).ToList();
                var nativeResult = DartGameAPI.Services.DartDetectNative.Detect(
                    1, "audit_bench", camIds, afterImages.Select(a => a.bytes).ToArray(), beforeImagesList.ToArray());

                if (nativeResult == null) continue;
                totalDarts++;

                int detSeg = nativeResult.Segment;
                int detMul = nativeResult.Multiplier;
                string method = nativeResult.Method ?? "unknown";
                double fx = nativeResult.CoordsX;
                double fy = nativeResult.CoordsY;
                double r_post = Math.Sqrt(fx * fx + fy * fy);

                // Extract clamp data from tri_debug extension data
                var ext = nativeResult.TriDebug?.ExtensionData;
                double x_preclamp_x = GetExtDouble(ext, "x_preclamp_x");
                double x_preclamp_y = GetExtDouble(ext, "x_preclamp_y");
                double r_pre = Math.Sqrt(x_preclamp_x * x_preclamp_x + x_preclamp_y * x_preclamp_y);
                bool clampApplied = GetExtBool(ext, "radial_clamp_applied");
                string clampReason = GetExtString(ext, "radial_clamp_reason");
                double r_bcwt = GetExtDouble(ext, "r_bcwt");
                double r_bestpair = GetExtDouble(ext, "r_bestpair");
                double radialDelta = GetExtDouble(ext, "radial_delta");

                // If no preclamp data (e.g. SingleCam), use post coords
                if (r_pre == 0 && r_post == 0)
                    r_pre = 0; // genuinely zero (SingleCam collapse)
                else if (r_pre == 0)
                    r_pre = r_post; // no clamp path

                // Normalize method bucket
                string bucket = method;
                if (method.StartsWith("MissOverride")) bucket = "MissOverride";
                else if (method.StartsWith("SingleCam")) bucket = "SingleCam";
                else if (method.StartsWith("WHRS")) bucket = "WHRS";
                else if (method.Contains("RadialClamp")) bucket = "RadialClamp";

                if (!methodStats.ContainsKey(bucket))
                    methodStats[bucket] = new MethodAuditStats { Method = bucket };
                var ms = methodStats[bucket];
                ms.Count++;
                ms.SumRPre += r_pre;
                ms.SumClampDelta += Math.Abs(r_post - r_pre);
                if (r_post == 0 && fx == 0 && fy == 0) ms.ZeroRCount++;
                if (r_post > 1.0) ms.OverDoubleCount++;
                bool isCorrect = (detSeg == truthSeg && detMul == truthMul);
                if (!isCorrect && detSeg == truthSeg && detMul != truthMul && !(detSeg == 0 && truthSeg == 0))
                    ms.RingErrorCount++;

                // Ring error detail
                bool isRingError = (detSeg == truthSeg && detMul != truthMul && !(detSeg == 0 && truthSeg == 0) && detMul != 0);
                if (isRingError)
                {
                    var (nearName, nearDist) = NearestBoundary(r_post);
                    var (nearNamePre, nearDistPre) = NearestBoundary(r_pre);
                    ringErrorDetails.Add(new
                    {
                        dart_id = $"{gId}/{roundFolder}/{dartFolder}",
                        method,
                        method_bucket = bucket,
                        r_pre_clamp = Math.Round(r_pre, 6),
                        r_pre_clamp_mm = Math.Round(r_pre * DOUBLE_OUTER_MM, 2),
                        r_post_clamp = Math.Round(r_post, 6),
                        r_post_clamp_mm = Math.Round(r_post * DOUBLE_OUTER_MM, 2),
                        clamp_applied = clampApplied,
                        clamp_reason = clampReason,
                        clamp_delta_mm = Math.Round(Math.Abs(r_post - r_pre) * DOUBLE_OUTER_MM, 2),
                        r_bcwt = Math.Round(r_bcwt, 6),
                        r_bcwt_mm = Math.Round(r_bcwt * DOUBLE_OUTER_MM, 2),
                        r_bestpair = Math.Round(r_bestpair, 6),
                        r_bestpair_mm = Math.Round(r_bestpair * DOUBLE_OUTER_MM, 2),
                        radial_delta_mm = Math.Round(radialDelta * DOUBLE_OUTER_MM, 2),
                        multiplier_by_r_pre = MultFromR(r_pre),
                        multiplier_by_r_post = MultFromR(r_post),
                        final_multiplier = detMul,
                        gt_multiplier = truthMul,
                        pred_segment = detSeg,
                        gt_segment = truthSeg,
                        nearest_boundary_post = nearName,
                        distance_to_boundary_post_mm = Math.Round(nearDist, 2),
                        nearest_boundary_pre = nearNamePre,
                        distance_to_boundary_pre_mm = Math.Round(nearDistPre, 2),
                        board_constants = new {
                            triple_inner_mm = TRIPLE_INNER_MM,
                            triple_outer_mm = TRIPLE_OUTER_MM,
                            double_inner_mm = DOUBLE_INNER_MM,
                            double_outer_mm = DOUBLE_OUTER_MM
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Radial audit error for {Path}: {Error}", fullPath, ex.Message);
            }
        }

        DartGameAPI.Services.DartDetectNative.ClearBoard("audit_bench");

        // Build by_method summary
        var byMethod = new Dictionary<string, object>();
        foreach (var (bucket, ms) in methodStats)
        {
            byMethod[bucket] = new
            {
                count = ms.Count,
                avg_r_pre_clamp = ms.Count > 0 ? Math.Round(ms.SumRPre / ms.Count, 6) : 0,
                avg_r_pre_clamp_mm = ms.Count > 0 ? Math.Round(ms.SumRPre / ms.Count * DOUBLE_OUTER_MM, 2) : 0,
                avg_clamp_delta_mm = ms.Count > 0 ? Math.Round(ms.SumClampDelta / ms.Count * DOUBLE_OUTER_MM, 2) : 0,
                ring_error_count = ms.RingErrorCount,
                ring_error_rate_pct = ms.Count > 0 ? Math.Round(100.0 * ms.RingErrorCount / ms.Count, 1) : 0,
                zero_r_count = ms.ZeroRCount,
                over_double_count = ms.OverDoubleCount
            };
        }

        var audit = new
        {
            total_darts = totalDarts,
            ring_error_count = ringErrorDetails.Count,
            board_constants = new {
                triple_inner_mm = TRIPLE_INNER_MM,
                triple_outer_mm = TRIPLE_OUTER_MM,
                double_inner_mm = DOUBLE_INNER_MM,
                double_outer_mm = DOUBLE_OUTER_MM
            },
            by_method = byMethod,
            ring_error_details = ringErrorDetails
        };

        var outputPath = Path.Combine(basePath, "radial_audit.json");
        var jsonStr = JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(outputPath, jsonStr);
        _logger.LogInformation("RADIAL-AUDIT: Wrote {Path}", outputPath);

        return Ok(audit);
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
    [JsonPropertyName("dart_details")] public List<ReplayDartDetail>? DartDetails { get; set; }
    [JsonPropertyName("config_hash")] public string ConfigHash { get; set; } = "";
    [JsonPropertyName("assertions_skipped")] public int AssertionsSkipped { get; set; }
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

public class ReplayDartDetail
{
    [JsonPropertyName("round")] public string Round { get; set; } = "";
    [JsonPropertyName("dart")] public string Dart { get; set; } = "";
    [JsonPropertyName("truth_segment")] public int TruthSegment { get; set; }
    [JsonPropertyName("truth_multiplier")] public int TruthMultiplier { get; set; }
    [JsonPropertyName("detected_segment")] public int DetectedSegment { get; set; }
    [JsonPropertyName("detected_multiplier")] public int DetectedMultiplier { get; set; }
    [JsonPropertyName("correct")] public bool Correct { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("coords_x")] public double CoordsX { get; set; }
    [JsonPropertyName("coords_y")] public double CoordsY { get; set; }
    [JsonPropertyName("camera_details")] public Dictionary<string, CameraDetail>? CameraDetails { get; set; }
    [JsonPropertyName("tri_debug")] public TriangulationDebugInfo? TriDebug { get; set; }
    [JsonPropertyName("detect_ms")] public long DetectMs { get; set; }
    [JsonPropertyName("config_hash")] public string ConfigHash { get; set; } = "";
    [JsonPropertyName("enabled_stack")] public List<string> EnabledStack { get; set; } = new();
    [JsonPropertyName("assertions_passed")] public bool AssertionsPassed { get; set; } = true;
    [JsonPropertyName("assertion_issues")] public List<string> AssertionIssues { get; set; } = new();
}

public class DebugDetectRequest
{
    public List<DebugImagePayload> Images { get; set; } = new();
    public List<DebugImagePayload> BeforeImages { get; set; } = new();

}

public class DebugImagePayload
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; 
} = string.Empty;
}

public class MethodAuditStats
{
    public string Method { get; set; } = "";
    public int Count { get; set; }
    public double SumRPre { get; set; }
    public double SumClampDelta { get; set; }
    public int RingErrorCount { get; set; }
    public int ZeroRCount { get; set; }
    public int OverDoubleCount { get; set; }
}

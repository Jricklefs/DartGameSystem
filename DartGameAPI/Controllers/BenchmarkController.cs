using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using DartGameAPI.Services;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/benchmark")]
public class BenchmarkController : ControllerBase
{
    private readonly BenchmarkSettings _settings;
    private readonly ILogger<BenchmarkController> _logger;
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public BenchmarkController(BenchmarkSettings settings, ILogger<BenchmarkController> logger)
    {
        _settings = settings;
        _logger = logger;
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
}

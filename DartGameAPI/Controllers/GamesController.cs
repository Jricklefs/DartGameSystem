using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Models;
using DartGameAPI.Services;
using DartGameAPI.Hubs;
using DartGameAPI.Data;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GamesController : ControllerBase
{
    private readonly GameService _gameService;
    private readonly DartDetectClient _dartDetectClient;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly DartsMobDbContext _db;
    private readonly ILogger<GamesController> _logger;

    public GamesController(
        GameService gameService, 
        DartDetectClient dartDetectClient,
        IHubContext<GameHub> hubContext, 
        DartsMobDbContext db, 
        ILogger<GamesController> logger)
    {
        _gameService = gameService;
        _dartDetectClient = dartDetectClient;
        _hubContext = hubContext;
        _db = db;
        _logger = logger;
    }

    // ===== HUB ENDPOINT - Receives images from DartSensor =====

    /// <summary>
    /// Hub detect endpoint - receives images from DartSensor, forwards to DartDetect.
    /// Hub-and-spoke: Sensor → GameAPI → DetectAPI
    /// </summary>
    [HttpPost("detect")]
    public async Task<ActionResult> Detect([FromBody] DetectRequest request)
    {
        _logger.LogDebug("Received detect request with {Count} images from board {BoardId}", 
            request.Images?.Count ?? 0, request.BoardId);

        var game = _gameService.GetGameForBoard(request.BoardId ?? "default");
        if (game == null)
        {
            return Ok(new { message = "No active game", darts = new List<object>() });
        }

        if (game.State != GameState.InProgress)
        {
            return Ok(new { message = "Game not in progress", darts = new List<object>() });
        }

        // Forward images to DartDetect API
        var images = request.Images?.Select(i => new CameraImageDto
        {
            CameraId = i.CameraId,
            Image = i.Image
        }).ToList() ?? new List<CameraImageDto>();

        var detectResult = await _dartDetectClient.DetectAsync(images);
        
        if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            return Ok(new { message = "No darts detected", darts = new List<object>() });
        }

        var processedDarts = new List<object>();
        
        foreach (var tip in detectResult.Tips.Where(t => t.Confidence > 0.5))
        {
            if (IsKnownDart(game, tip.XMm, tip.YMm)) continue;

            var dart = new DartThrow
            {
                Index = game.CurrentTurn?.Darts.Count ?? 0,
                Segment = tip.Segment,
                Multiplier = tip.Multiplier,
                Zone = tip.Zone,
                Score = tip.Score,
                XMm = tip.XMm,
                YMm = tip.YMm,
                Confidence = tip.Confidence
            };

            game.KnownDarts.Add(new KnownDart
            {
                XMm = tip.XMm,
                YMm = tip.YMm,
                Score = tip.Score,
                DetectedAt = DateTime.UtcNow
            });

            _gameService.ApplyManualDart(game, dart);
            await _hubContext.SendDartThrown(game.BoardId, dart, game);
            processedDarts.Add(new { dart.Zone, dart.Score, dart.Segment, dart.Multiplier });
            
            _logger.LogInformation("Dart processed via hub: {Zone} = {Score}", dart.Zone, dart.Score);

            if (game.State == GameState.Finished)
            {
                await _hubContext.SendGameEnded(game.BoardId, game);
            }
        }

        return Ok(new { message = processedDarts.Any() ? "Darts detected" : "No new darts", darts = processedDarts });
    }

    /// <summary>
    /// Health check for DartSensor
    /// </summary>
    [HttpGet("health")]
    public ActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Event: Board cleared - triggers rebase
    /// </summary>
    [HttpPost("events/clear")]
    public async Task<ActionResult> EventBoardClear([FromBody] BoardEventRequest request)
    {
        var boardId = request.BoardId ?? "default";
        _gameService.ClearBoard(boardId);
        
        // Tell sensor to rebase via SignalR
        await _hubContext.SendRebase(boardId);
        await _hubContext.SendBoardCleared(boardId);
        
        return Ok(new { message = "Board cleared, rebase triggered via SignalR" });
    }

    /// <summary>
    /// Event: Dart detected (tracking)
    /// </summary>
    [HttpPost("events/dart")]
    public ActionResult EventDartDetected([FromBody] DartEventRequest request)
    {
        _logger.LogDebug("Dart event: board {BoardId}, index {Index}", request.BoardId, request.DartIndex);
        return Ok(new { message = "Dart event recorded" });
    }

    // ===== GAME MANAGEMENT ENDPOINTS =====

    /// <summary>
    /// Get recent games with player info
    /// </summary>
    [HttpGet("recent")]
    public async Task<ActionResult> GetRecentGames([FromQuery] int count = 20)
    {
        var today = DateTime.UtcNow.Date;
        
        var games = await _db.Games
            .Where(g => g.GameState == 2) // Completed
            .OrderByDescending(g => g.EndedAt)
            .Take(count)
            .Select(g => new
            {
                gameId = g.GameId,
                gameMode = g.GameMode,
                startedAt = g.StartedAt,
                endedAt = g.EndedAt,
                durationSeconds = g.DurationSeconds,
                winnerId = g.WinnerPlayerId
            })
            .ToListAsync();

        var gameIds = games.Select(g => g.gameId).ToList();
        var gamePlayers = await _db.GamePlayers
            .Where(gp => gameIds.Contains(gp.GameId))
            .Join(_db.Players, gp => gp.PlayerId, p => p.PlayerId, (gp, p) => new { gp, p })
            .Select(x => new
            {
                x.gp.GameId,
                x.gp.PlayerId,
                x.gp.PlayerOrder,
                x.gp.FinalScore,
                x.gp.IsWinner,
                x.gp.HighestTurn,
                Name = x.p.Nickname
            })
            .ToListAsync();

        var result = games.Select(g =>
        {
            var players = gamePlayers.Where(p => p.GameId == g.gameId).OrderBy(p => p.PlayerOrder).ToList();
            return new
            {
                g.gameId,
                g.gameMode,
                g.endedAt,
                g.durationSeconds,
                player1 = players.ElementAtOrDefault(0) is var p1 && p1 != null ? new { name = p1.Name, score = p1.IsWinner ? 3 : (3 - 1), isWinner = p1.IsWinner } : null,
                player2 = players.ElementAtOrDefault(1) is var p2 && p2 != null ? new { name = p2.Name, score = p2.IsWinner ? 3 : (3 - 1), isWinner = p2.IsWinner } : null,
                winner = players.FirstOrDefault(p => p.IsWinner)?.Name
            };
        });

        var gamesToday = await _db.Games.CountAsync(g => g.StartedAt >= today);
        var totalGames = await _db.Games.CountAsync();

        return Ok(new { 
            games = result, 
            gamesToday, 
            totalGames 
        });
    }

    /// <summary>
    /// Get all registered boards
    /// </summary>
    [HttpGet("boards")]
    public async Task<ActionResult<IEnumerable<BoardDto>>> GetBoards()
    {
        var boards = await _db.Boards
            .Where(b => b.IsActive)
            .Select(b => new BoardDto
            {
                BoardId = b.BoardId,
                Name = b.Name,
                Location = b.Location,
                CameraCount = b.CameraCount,
                IsCalibrated = b.IsCalibrated,
                LastCalibration = b.LastCalibration
            })
            .ToListAsync();

        return Ok(boards);
    }

    /// <summary>
    /// Update board calibration status
    /// </summary>
    [HttpPut("boards/{boardId}/calibration")]
    public async Task<ActionResult> UpdateCalibration(string boardId, [FromBody] UpdateCalibrationRequest request)
    {
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId);
        if (board == null) return NotFound();

        board.IsCalibrated = request.IsCalibrated;
        board.LastCalibration = request.IsCalibrated ? DateTime.UtcNow : board.LastCalibration;
        if (request.CalibrationData != null)
        {
            board.CalibrationData = request.CalibrationData;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Calibration updated" });
    }

    /// <summary>
    /// Pre-flight check - validate system is ready for a game
    /// </summary>
    [HttpGet("preflight/{boardId}")]
    public async Task<ActionResult<PreflightResult>> PreflightCheck(string boardId)
    {
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId && b.IsActive);
        
        var cameras = await _db.Cameras
            .Where(c => c.BoardId == boardId && c.IsActive)
            .Select(c => new { c.CameraId, c.IsCalibrated, c.CalibrationQuality })
            .ToListAsync();
        
        var sensorConnected = GameHub.IsSensorConnected(boardId);
        var allCalibrated = cameras.All(c => c.IsCalibrated) && cameras.Count > 0;
        
        var issues = new List<PreflightIssue>();
        
        if (board == null)
        {
            issues.Add(new PreflightIssue { 
                Code = "BOARD_NOT_FOUND", 
                Message = "Board not registered",
                Severity = "error"
            });
        }
        
        if (cameras.Count == 0)
        {
            issues.Add(new PreflightIssue { 
                Code = "NO_CAMERAS", 
                Message = "No cameras registered",
                Severity = "error"
            });
        }
        else
        {
            var uncalibrated = cameras.Where(c => !c.IsCalibrated).Select(c => c.CameraId).ToList();
            if (uncalibrated.Any())
            {
                issues.Add(new PreflightIssue { 
                    Code = "NOT_CALIBRATED", 
                    Message = $"Cameras not calibrated: {string.Join(", ", uncalibrated)}",
                    Severity = "error",
                    Details = uncalibrated
                });
            }
        }
        
        if (!sensorConnected)
        {
            issues.Add(new PreflightIssue { 
                Code = "SENSOR_DISCONNECTED", 
                Message = "Sensor not connected",
                Severity = "error"
            });
        }
        
        return Ok(new PreflightResult
        {
            BoardId = boardId,
            CanStart = issues.Count == 0,
            CameraCount = cameras.Count,
            CalibratedCount = cameras.Count(c => c.IsCalibrated),
            SensorConnected = sensorConnected,
            Issues = issues
        });
    }

    /// <summary>
    /// Create a new game - validates cameras are calibrated and sensor is connected
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Game>> CreateGame([FromBody] CreateGameRequest request)
    {
        try
        {
            var boardId = request.BoardId ?? "default";
            
            // 1. Check board exists
            var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId && b.IsActive);
            if (board == null)
            {
                // Auto-create default board if it doesn't exist
                if (boardId == "default")
                {
                    board = new BoardEntity
                    {
                        BoardId = "default",
                        Name = "Default Board",
                        CameraCount = 0,
                        IsCalibrated = false,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.Boards.Add(board);
                    await _db.SaveChangesAsync();
                }
                else
                {
                    return NotFound(new { 
                        error = "Board not found", 
                        code = "BOARD_NOT_FOUND",
                        boardId 
                    });
                }
            }
            
            // 2. Check cameras are registered
            var cameras = await _db.Cameras
                .Where(c => c.BoardId == boardId && c.IsActive)
                .ToListAsync();
            
            if (cameras.Count == 0)
            {
                return BadRequest(new { 
                    error = "No cameras registered", 
                    code = "NO_CAMERAS",
                    message = "Please register cameras in Settings before starting a game",
                    boardId
                });
            }
            
            // 3. Check all cameras are calibrated
            var uncalibrated = cameras.Where(c => !c.IsCalibrated).Select(c => c.CameraId).ToList();
            if (uncalibrated.Any())
            {
                return BadRequest(new { 
                    error = "Cameras not calibrated", 
                    code = "NOT_CALIBRATED",
                    uncalibratedCameras = uncalibrated,
                    message = $"Please calibrate cameras before starting: {string.Join(", ", uncalibrated)}",
                    boardId
                });
            }
            
            // 4. Check sensor is connected
            if (!GameHub.IsSensorConnected(boardId))
            {
                return BadRequest(new { 
                    error = "Sensor not connected", 
                    code = "SENSOR_DISCONNECTED",
                    message = "DartSensor is not connected. Please start the sensor and wait for it to connect.",
                    boardId
                });
            }
            
            // 5. All checks passed - create the game
            var game = _gameService.CreateGame(boardId, request.Mode, request.PlayerNames, request.BestOf);
            
            // 6. Notify connected clients AND sensor via SignalR
            await _hubContext.SendGameStarted(boardId, game);
            _logger.LogInformation("Game {GameId} created on board {BoardId} - sensor notified", game.Id, boardId);
            
            return Ok(game);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message, code = "GAME_ERROR" });
        }
    }

    /// <summary>
    /// Get game state
    /// </summary>
    [HttpGet("{id}")]
    public ActionResult<Game> GetGame(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        return Ok(game);
    }

    /// <summary>
    /// Get current game for a board
    /// </summary>
    [HttpGet("board/{boardId}")]
    public ActionResult<Game> GetGameForBoard(string boardId)
    {
        var game = _gameService.GetGameForBoard(boardId);
        if (game == null) return NotFound(new { message = "No active game on this board" });
        return Ok(game);
    }

    /// <summary>
    /// End a game
    /// </summary>
    [HttpPost("{id}/end")]
    public async Task<ActionResult> EndGame(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        
        var boardId = game.BoardId;
        _gameService.EndGame(id);
        
        // Notify connected clients
        await _hubContext.SendGameEnded(boardId, game);
        
        return Ok(new { message = "Game ended" });
    }

    /// <summary>
    /// Advance to next player's turn (Next button)
    /// </summary>
    [HttpPost("{id}/next-turn")]
    public async Task<ActionResult> NextTurn(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });
        
        var previousTurn = game.CurrentTurn ?? new Turn();
        
        _gameService.NextTurn(game);
        
        // Notify connected clients that turn ended (this also sends Rebase to sensor via SignalR)
        await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        _logger.LogInformation("Turn ended on board {BoardId}, sensor rebase triggered via SignalR", game.BoardId);
        
        return Ok(new { game = game });
    }

    /// <summary>
    /// Clear the board (darts removed) - triggers rebase
    /// </summary>
    [HttpPost("board/{boardId}/clear")]
    public async Task<ActionResult> ClearBoard(string boardId)
    {
        _gameService.ClearBoard(boardId);
        
        // Tell sensor to rebase via SignalR
        await _hubContext.SendRebase(boardId);
        await _hubContext.SendBoardCleared(boardId);
        _logger.LogInformation("Board {BoardId} cleared, rebase triggered via SignalR", boardId);
        
        return Ok(new { message = "Board cleared, rebase triggered" });
    }

    /// <summary>
    /// Manual dart entry (for testing without cameras)
    /// </summary>
    [HttpPost("{id}/manual")]
    public async Task<ActionResult<ThrowResult>> ManualThrow(string id, [FromBody] ManualThrowRequest request)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });

        var dart = new DartThrow
        {
            Index = game.CurrentTurn?.Darts.Count ?? 0,
            Segment = request.Segment,
            Multiplier = request.Multiplier,
            Zone = GetZoneName(request.Segment, request.Multiplier),
            Score = request.Segment * request.Multiplier,
            XMm = 0,
            YMm = 0,
            Confidence = 1.0
        };

        _gameService.ApplyManualDart(game, dart);
        
        await _hubContext.SendDartThrown(game.BoardId, dart, game);
        
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }

        return Ok(new ThrowResult { NewDart = dart, Game = game });
    }

    /// <summary>
    /// Correct a dart in the current turn
    /// </summary>
    [HttpPost("{id}/correct")]
    public async Task<ActionResult<ThrowResult>> CorrectDart(string id, [FromBody] CorrectDartRequest request)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });
        if (game.CurrentTurn == null)
            return BadRequest(new { error = "No current turn" });
        if (request.DartIndex < 0 || request.DartIndex >= game.CurrentTurn.Darts.Count)
            return BadRequest(new { error = "Invalid dart index" });

        var oldDart = game.CurrentTurn.Darts[request.DartIndex];
        var oldScore = oldDart.Score;
        
        var newDart = new DartThrow
        {
            Index = request.DartIndex,
            Segment = request.Segment,
            Multiplier = request.Multiplier,
            Zone = GetZoneName(request.Segment, request.Multiplier),
            Score = request.Segment * request.Multiplier,
            XMm = oldDart.XMm,
            YMm = oldDart.YMm,
            Confidence = 1.0
        };

        _gameService.CorrectDart(game, request.DartIndex, newDart);
        
        _logger.LogInformation("Corrected dart {Index}: {OldZone}={OldScore} -> {NewZone}={NewScore}", 
            request.DartIndex, oldDart.Zone, oldScore, newDart.Zone, newDart.Score);

        await _hubContext.SendDartThrown(game.BoardId, newDart, game);
        
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }

        return Ok(new ThrowResult { NewDart = newDart, Game = game });
    }

    // === Private helpers ===

    private bool IsKnownDart(Game game, double xMm, double yMm, double thresholdMm = 20.0)
    {
        foreach (var known in game.KnownDarts)
        {
            var dist = Math.Sqrt(Math.Pow(xMm - known.XMm, 2) + Math.Pow(yMm - known.YMm, 2));
            if (dist < thresholdMm) return true;
        }
        return false;
    }

    private static string GetZoneName(int segment, int multiplier)
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

// === Request/Response DTOs ===

public class DetectRequest
{
    public string? BoardId { get; set; }
    public List<ImagePayload>? Images { get; set; }
}

public class ImagePayload
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
}

public class BoardEventRequest
{
    public string? BoardId { get; set; }
}

public class DartEventRequest
{
    public string? BoardId { get; set; }
    public int DartIndex { get; set; }
}

public class CreateGameRequest
{
    public string BoardId { get; set; } = "default";
    public GameMode Mode { get; set; } = GameMode.Practice;
    public List<string> PlayerNames { get; set; } = new();
    public int BestOf { get; set; } = 5;
}

public class ManualThrowRequest
{
    public int Segment { get; set; }
    public int Multiplier { get; set; } = 1;
}

public class CorrectDartRequest
{
    public int DartIndex { get; set; }
    public int Segment { get; set; }
    public int Multiplier { get; set; } = 1;
}

public class ThrowResult
{
    public DartThrow? NewDart { get; set; }
    public Game Game { get; set; } = null!;
}

public class BoardDto
{
    public string BoardId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public int CameraCount { get; set; }
    public bool IsCalibrated { get; set; }
    public DateTime? LastCalibration { get; set; }
}

public class UpdateCalibrationRequest
{
    public bool IsCalibrated { get; set; }
    public string? CalibrationData { get; set; }
}

public class PreflightResult
{
    public string BoardId { get; set; } = string.Empty;
    public bool CanStart { get; set; }
    public int CameraCount { get; set; }
    public int CalibratedCount { get; set; }
    public bool SensorConnected { get; set; }
    public List<PreflightIssue> Issues { get; set; } = new();
}

public class PreflightIssue
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public List<string>? Details { get; set; }
}

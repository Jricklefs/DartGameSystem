using System.Net.Http.Json;
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
    private readonly IHttpClientFactory _httpClientFactory;

    public GamesController(
        GameService gameService, 
        DartDetectClient dartDetectClient,
        IHubContext<GameHub> hubContext, 
        DartsMobDbContext db, 
        ILogger<GamesController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _gameService = gameService;
        _dartDetectClient = dartDetectClient;
        _hubContext = hubContext;
        _db = db;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    // ===== HUB ENDPOINT - Receives images from DartSensor =====

    /// <summary>
    /// Hub detect endpoint - receives images from DartSensor, forwards to DartDetect.
    /// Hub-and-spoke: Sensor → GameAPI → DetectAPI
    /// 
    /// DartSensor only calls this when motion detected (new dart landed).
    /// DartDetect now handles differential detection and returns only the NEW dart.
    /// We just need to score whatever DartDetect returns.
    /// </summary>
    [HttpPost("detect")]
    public async Task<ActionResult> Detect([FromBody] DetectRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestId = request.RequestId ?? Guid.NewGuid().ToString()[..8];
        _logger.LogInformation("[TIMING][{RequestId}] DG: Received detect @ epoch={Epoch}", requestId, epochMs);
        
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
        
        // Forward before images if provided (for clean diff detection)
        var beforeImages = request.BeforeImages?.Select(i => new CameraImageDto
        {
            CameraId = i.CameraId,
            Image = i.Image
        }).ToList();

        // Get dart number for differential detection (darts already scored + 1)
        var dartsThisTurn = game.CurrentTurn?.Darts ?? new List<DartThrow>();
        var dartNumber = dartsThisTurn.Count + 1;
        var boardId = request.BoardId ?? "default";

        // Update benchmark context for DartDetect (fire and forget)
        var player = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
        _ = UpdateBenchmarkContext(boardId, game.Id, game.CurrentRound, player?.Name);

        var ddStartEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _logger.LogInformation("[TIMING][{RequestId}] DG: Calling DartDetect @ epoch={Epoch} (prep={Prep}ms)", 
            requestId, ddStartEpoch, sw.ElapsedMilliseconds);
        var detectResult = await _dartDetectClient.DetectAsync(images, boardId, dartNumber, beforeImages);
        var ddEndEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddDuration = ddEndEpoch - ddStartEpoch;
        _logger.LogInformation("[TIMING][{RequestId}] DG: DartDetect returned @ epoch={Epoch} (took={Took}ms)", 
            requestId, ddEndEpoch, ddDuration);
        
        if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            return Ok(new { message = "No darts detected", darts = new List<object>() });
        }

        _logger.LogDebug("DartDetect returned {TipCount} tip(s), turn has {DartCount} darts scored",
            detectResult.Tips.Count, dartsThisTurn.Count);

        // DartDetect now handles differential detection - it only returns the NEW dart(s).
        // DartDetect already filters by YOLO confidence, so we trust what it returns.
        // Just take the most confident tip.
        var newTip = detectResult.Tips
            .OrderByDescending(t => t.Confidence)
            .FirstOrDefault();

        if (newTip == null)
        {
            return Ok(new { message = "No new darts", darts = new List<object>() });
        }

        // Score the new dart
        var dart = new DartThrow
        {
            Index = dartsThisTurn.Count,
            Segment = newTip.Segment,
            Multiplier = newTip.Multiplier,
            Zone = newTip.Zone,
            Score = newTip.Score,
            XMm = newTip.XMm,
            YMm = newTip.YMm,
            Confidence = newTip.Confidence
        };

        _gameService.ApplyManualDart(game, dart);
        await _hubContext.SendDartThrown(game.BoardId, dart, game);
        
        _logger.LogInformation("New dart scored: {Zone} {Segment}x{Mult} = {Score}", 
            dart.Zone, dart.Segment, dart.Multiplier, dart.Score);

        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }
        else if (game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }

        return Ok(new { 
            message = "Dart detected", 
            darts = new[] { new { dart.Zone, dart.Score, dart.Segment, dart.Multiplier } }
        });
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
    /// Event: Board cleared - triggers rebase and advances turn
    /// When player clears board (pulls darts), their turn is over
    /// </summary>
    [HttpPost("events/clear")]
    public async Task<ActionResult> EventBoardClear([FromBody] BoardEventRequest request)
    {
        var boardId = request.BoardId ?? "default";
        var game = _gameService.GetGameForBoard(boardId);
        var previousTurn = game?.CurrentTurn ?? new Turn();
        var dartCount = previousTurn.Darts?.Count ?? 0;
        var isBusted = previousTurn.IsBusted;
        
        _gameService.ClearBoard(boardId);
        
        // Clear DartDetect cache for clean differential detection
        try 
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            await client.PostAsJsonAsync("http://127.0.0.1:8000/v1/clear", new { board_id = boardId });
            _logger.LogDebug("DartDetect cache cleared for board {BoardId}", boardId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to clear DartDetect cache: {Error}", ex.Message);
        }
        
        // Tell sensor to rebase via SignalR
        await _hubContext.SendRebase(boardId);
        
        // Always advance turn when board is cleared (player pulled their darts)
        // Exception: if busted, wait for bust confirmation from UI
        if (game != null && !isBusted)
        {
            _gameService.NextTurn(game);
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
            _logger.LogInformation("Board cleared - {DartCount} darts thrown, advancing to next player", dartCount);
        }
        else if (game != null && isBusted)
        {
            // Busted - just notify board cleared, wait for bust confirmation
            await _hubContext.SendBoardCleared(boardId);
            _logger.LogInformation("Board cleared but player busted - waiting for bust confirmation");
        }
        else
        {
            await _hubContext.SendBoardCleared(boardId);
        }
        
        return Ok(new { message = "Board cleared", turnAdvanced = game != null && !isBusted, dartCount });
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
        
        // Check sensor via HTTP health check (DartSensor is HTTP-only, not SignalR)
        var sensorConnected = false;
        try
        {
            using var sensorClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var sensorResp = await sensorClient.GetAsync("http://localhost:8001/status");
            sensorConnected = sensorResp.IsSuccessStatusCode;
        }
        catch { /* sensor not reachable */ }
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
            // Check sensor via HTTP
            var sensorUp = false;
            try
            {
                using var sc = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                sensorUp = (await sc.GetAsync("http://localhost:8001/status")).IsSuccessStatusCode;
            }
            catch { }
            if (!sensorUp)
            {
                return BadRequest(new { 
                    error = "Sensor not connected", 
                    code = "SENSOR_DISCONNECTED",
                    message = "DartSensor is not connected. Please start the sensor and wait for it to connect.",
                    boardId
                });
            }
            
            // 5. All checks passed - create the game
            var game = _gameService.CreateGame(boardId, request.Mode, request.PlayerNames, request.BestOf, request.RequireDoubleOut);
            
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
        
        // Update benchmark context with new round/player
        var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
        _ = UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
        
        // Notify connected clients that turn ended (this also sends Rebase to sensor via SignalR)
        await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        _logger.LogInformation("Turn ended on board {BoardId}, sensor rebase triggered via SignalR", game.BoardId);
        
        return Ok(new { game = game });
    }

    /// <summary>
    /// Confirm bust and end turn (called after player acknowledges bust)
    /// </summary>
    [HttpPost("{id}/confirm-bust")]
    public async Task<ActionResult> ConfirmBust(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });
        
        if (game.CurrentTurn == null || !game.CurrentTurn.IsBusted)
            return BadRequest(new { error = "Current turn is not busted" });
        
        var previousTurn = game.CurrentTurn;
        
        _gameService.ConfirmBust(game);
        
        // Update benchmark context with new round/player
        var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
        _ = UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
        
        // Notify connected clients that turn ended
        await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        _logger.LogInformation("Bust confirmed on board {BoardId}, turn ended", game.BoardId);
        
        return Ok(new { game = game });
    }

    /// <summary>
    /// Clear the board (darts removed) - triggers rebase
    /// </summary>
    [HttpPost("board/{boardId}/clear")]
    public async Task<ActionResult> ClearBoard(string boardId)
    {
        var game = _gameService.GetGameForBoard(boardId);
        var turnWasComplete = game?.CurrentTurn?.IsComplete == true;
        var previousTurn = game?.CurrentTurn ?? new Turn();
        
        _gameService.ClearBoard(boardId);
        
        // If turn was complete (3 darts), update context BEFORE signaling rebase
        if (turnWasComplete && game != null)
        {
            // Update benchmark context with new round/player - MUST complete before rebase
            var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
            await UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
        }
        
        // Tell sensor to rebase via SignalR (AFTER context is updated)
        await _hubContext.SendRebase(boardId);
        
        // Notify clients
        if (turnWasComplete && game != null)
        {
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
            _logger.LogInformation("Board {BoardId} cleared, turn complete - advancing to next player", boardId);
        }
        else
        {
            await _hubContext.SendBoardCleared(boardId);
            _logger.LogInformation("Board {BoardId} cleared (mid-turn), rebase triggered", boardId);
        }
        
        return Ok(new { message = "Board cleared", turnAdvanced = turnWasComplete });
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
        else if (game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
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

        // Record correction for benchmark analysis (fire and forget)
        _ = RecordBenchmarkCorrection(game, request.DartIndex, oldDart, newDart);

        await _hubContext.SendDartThrown(game.BoardId, newDart, game);
        
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }
        else if (game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }

        return Ok(new ThrowResult { NewDart = newDart, Game = game });
    }

    /// <summary>
    /// Record dart correction to DartDetect benchmark system
    /// </summary>
    private async Task RecordBenchmarkCorrection(Game game, int dartIndex, DartThrow oldDart, DartThrow newDart)
    {
        try
        {
            var dartNumber = dartIndex + 1;
            _logger.LogInformation("Recording benchmark correction for dart {DartNumber}: {OldSeg}x{OldMult} -> {NewSeg}x{NewMult}", 
                dartNumber, oldDart.Segment, oldDart.Multiplier, newDart.Segment, newDart.Multiplier);
            
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            
            var payload = new
            {
                dart_number = dartNumber,
                game_id = game.Id,
                original_segment = oldDart.Segment,
                original_multiplier = oldDart.Multiplier,
                corrected_segment = newDart.Segment,
                corrected_multiplier = newDart.Multiplier
            };
            
            var response = await client.PostAsJsonAsync("http://127.0.0.1:8000/v1/benchmark/correction", payload);
            _logger.LogInformation("Benchmark correction response: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to record benchmark correction: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Update benchmark context in DartDetect for proper file organization
    /// </summary>
    private async Task UpdateBenchmarkContext(string boardId, string gameId, int round, string? playerName)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            
            var payload = new
            {
                board_id = boardId,
                game_id = gameId,
                round_num = round,
                player_name = playerName ?? "player"
            };
            
            _logger.LogInformation("Setting benchmark context: board={BoardId}, game={GameId}, round={Round}, player={Player}", 
                boardId, gameId, round, playerName ?? "player");
            
            var response = await client.PostAsJsonAsync("http://127.0.0.1:8000/v1/benchmark/context", payload);
            _logger.LogInformation("Benchmark context response: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to update benchmark context: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Remove a false dart from the current turn (phantom detection)
    /// </summary>
    [HttpPost("{id}/remove-dart")]
    public async Task<ActionResult<RemoveDartResult>> RemoveDart(string id, [FromBody] RemoveDartRequest request)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });
        if (game.CurrentTurn == null)
            return BadRequest(new { error = "No current turn" });
        if (request.DartIndex < 0 || request.DartIndex >= game.CurrentTurn.Darts.Count)
            return BadRequest(new { error = "Invalid dart index" });

        var removedDart = _gameService.RemoveDart(game, request.DartIndex);
        if (removedDart == null)
            return BadRequest(new { error = "Failed to remove dart" });
        
        _logger.LogInformation("Removed false dart {Index}: {Zone}={Score}", 
            request.DartIndex, removedDart.Zone, removedDart.Score);

        // Exclude from benchmark data in DartDetect
        try
        {
            using var httpClient = new HttpClient();
            var excludePayload = new
            {
                game_id = id,
                dart_index = request.DartIndex,
                reason = "false_detection_removed"
            };
            var json = System.Text.Json.JsonSerializer.Serialize(excludePayload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var dartDetectUrl = Environment.GetEnvironmentVariable("DARTDETECT_URL") ?? "http://localhost:8000";
            await httpClient.PostAsync($"{dartDetectUrl}/v1/benchmark/exclude-dart", content);
            _logger.LogInformation("Excluded dart from benchmark data");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to exclude dart from benchmark (non-fatal)");
        }

        // Notify clients about the update
        await _hubContext.SendDartRemoved(game.BoardId, removedDart, game);

        return Ok(new RemoveDartResult { RemovedDart = removedDart, Game = game });
    }

    // === Private helpers ===

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
    public List<ImagePayload>? BeforeImages { get; set; }  // Frames before dart landed
    public string? RequestId { get; set; }  // For cross-API timing correlation
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
    public bool RequireDoubleOut { get; set; } = false;
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

public class RemoveDartRequest
{
    public int DartIndex { get; set; }
}

public class RemoveDartResult
{
    public DartThrow? RemovedDart { get; set; }
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

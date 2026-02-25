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
    private readonly IDartDetectService _dartDetect;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly DartsMobDbContext _db;
    private readonly ILogger<GamesController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BenchmarkService _benchmark;
    private readonly X01GameEngine _x01Engine;
    private readonly CricketGameEngine _cricketEngine;

    public GamesController(
        GameService gameService, 
        IDartDetectService dartDetect,
        IHubContext<GameHub> hubContext, 
        DartsMobDbContext db, 
        ILogger<GamesController> logger,
        IHttpClientFactory httpClientFactory,
        BenchmarkService benchmark,
        X01GameEngine x01Engine,
        CricketGameEngine cricketEngine)
    {
        _gameService = gameService;
        _dartDetect = dartDetect;
        _hubContext = hubContext;
        _db = db;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _benchmark = benchmark;
        _x01Engine = x01Engine;
        _cricketEngine = cricketEngine;
    }

    // ===== HUB ENDPOINT - Receives images from DartSensor =====

    /// <summary>
    /// Hub detect endpoint - receives images from DartSensor, forwards to DartDetect.
    /// </summary>
    [HttpPost("detect")]
    public async Task<ActionResult> Detect([FromBody] DetectRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestId = request.RequestId ?? Guid.NewGuid().ToString()[..8];
        _logger.LogInformation("[TIMING][{RequestId}] DG: Received detect @ epoch={Epoch}", requestId, epochMs);
        
        var game = _gameService.GetGameForBoard(request.BoardId ?? "default");
        if (game == null)
            return Ok(new { message = "No active game", darts = new List<object>() });

        if (game.State != GameState.InProgress)
            return Ok(new { message = "Game not in progress", darts = new List<object>() });

        // === DART COUNT GUARD ===
        var guardDarts = game.CurrentTurn?.Darts ?? new List<DartThrow>();
        if (guardDarts.Count >= game.DartsPerTurn)
        {
            _logger.LogWarning("[GUARD] Rejected dart detection - turn already has {Count}/{Max} darts.",
                guardDarts.Count, game.DartsPerTurn);
            return Ok(new { message = "Turn complete - max darts reached", darts = new List<object>() });
        }

        // === BUST GUARD ===
        if (game.CurrentTurn?.IsBusted == true || game.CurrentTurn?.BustPending == true)
        {
            _logger.LogWarning("[GUARD] Rejected dart detection - turn is busted, waiting for confirmation.");
            return Ok(new { message = "Turn busted - waiting for confirmation", darts = new List<object>() });
        }

        // Forward images to DartDetect API
        var images = request.Images?.Select(i => new CameraImageDto
        {
            CameraId = i.CameraId,
            Image = i.Image
        }).ToList() ?? new List<CameraImageDto>();
        
        var beforeImages = request.BeforeImages?.Select(i => new CameraImageDto
        {
            CameraId = i.CameraId,
            Image = i.Image
        }).ToList();

        if (beforeImages == null || beforeImages.Count == 0)
        {
            _logger.LogWarning("[TIMING][{RequestId}] DG: Missing before images", requestId);
        }

        var dartsThisTurn = game.CurrentTurn?.Darts ?? new List<DartThrow>();
        var dartNumber = dartsThisTurn.Count + 1;
        var boardId = request.BoardId ?? "default";

        var player = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
        _ = UpdateBenchmarkContext(boardId, game.Id, game.CurrentRound, player?.Name);

        var ddStartEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _logger.LogInformation("[TIMING][{RequestId}] DG: Calling DartDetect @ epoch={Epoch}", requestId, ddStartEpoch);
        // Phase 4B: Convert multi-frame images if provided
        List<List<CameraImageDto>>? multiFrameImages = null;
        if (request.MultiFrameImages != null && request.MultiFrameImages.Count > 1)
        {
            multiFrameImages = request.MultiFrameImages.Select(frameSet =>
                frameSet.Select(i => new CameraImageDto { CameraId = i.CameraId, Image = i.Image }).ToList()
            ).ToList();
            _logger.LogInformation("[TIMING][{RequestId}] DG: Multi-frame with {Sets} frame sets", requestId, multiFrameImages.Count);
        }

        var detectResult = await _dartDetect.DetectAsync(images, boardId, dartNumber, beforeImages, multiFrameImages);
        var ddEndEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _logger.LogInformation("[TIMING][{RequestId}] DG: DartDetect returned (took={Took}ms)", requestId, ddEndEpoch - ddStartEpoch);
        
        if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            // Motion detected but no dart tip found ΓÇö record as a miss (score 0)
            _logger.LogInformation("[{RequestId}] No tip found ΓÇö recording as MISS", requestId);
            var missDart = new DartThrow
            {
                Index = dartsThisTurn.Count,
                Segment = 0,
                Multiplier = 0,
                Zone = "miss",
                Score = 0,
                XMm = 0,
                YMm = 0,
                Confidence = 0
            };

            if (game.IsX01Engine)
                _x01Engine.ProcessDart(game, missDart);
            else if (game.IsCricketEngine)
                _cricketEngine.ProcessDart(game, missDart);
            else
                _gameService.ApplyManualDart(game, missDart);
            
            await _hubContext.SendDartThrown(game.BoardId, missDart, game);

            // Save benchmark data for misses too
            if (_benchmark.IsEnabled)
            {
                var bmPlayer = player?.Name ?? "player";
                _ = Task.Run(() => _benchmark.SaveBenchmarkDataAsync(
                    requestId, dartNumber, boardId, game.Id, game.CurrentRound, bmPlayer,
                    request.BeforeImages, request.Images, null, detectResult, request.CameraSettings));
            }

            return Ok(new { message = "Miss recorded", darts = new[] { new { missDart.Zone, missDart.Score, missDart.Segment, missDart.Multiplier } } });
        }

        var newTip = detectResult.Tips.OrderByDescending(t => t.Confidence).FirstOrDefault();
        if (newTip == null)
        {
            await _hubContext.SendDartNotFound(boardId);
            return Ok(new { message = "No new darts", darts = new List<object>() });
        }

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

        var dartResult = _gameService.ApplyManualDart(game, dart);
        await _hubContext.SendDartThrown(game.BoardId, dart, game);

        // Save benchmark data
        if (_benchmark.IsEnabled)
        {
            var bmPlayer = player?.Name ?? "player";
            _ = Task.Run(() => _benchmark.SaveBenchmarkDataAsync(
                requestId, dartNumber, boardId, game.Id, game.CurrentRound, bmPlayer,
                request.BeforeImages, request.Images, newTip, detectResult, request.CameraSettings));  // BeforeImages=raw(with dart), Images=previous(before dart)
        }
        
        _logger.LogInformation("[TIMING][{RequestId}] DG: COMPLETE total={Total}ms", requestId, sw.ElapsedMilliseconds);

        // Handle game-ending / leg-winning results
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }
        else if (dartResult?.Type == DartResultType.Bust)
        {
            // Notify UI about bust
            await _hubContext.Clients.Group($"board:{game.BoardId}").SendAsync("BustDetected", new
            {
                playerId = dartResult.PendingBust?.PlayerId,
                reason = dartResult.BustReason,
                pendingBustId = dartResult.PendingBust?.Id,
                game = new { game.Id, game.State, game.CurrentPlayerIndex }
            });
        }
        else if (dartResult?.Type == DartResultType.LegWon)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }
        else if (game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }

        return Ok(new { 
            message = "Dart detected", 
            darts = new[] { new { dart.Zone, dart.Score, dart.Segment, dart.Multiplier } },
            result = dartResult?.Type.ToString()
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
    /// </summary>
    [HttpPost("events/clear")]
    public async Task<ActionResult> EventBoardClear([FromBody] BoardEventRequest request)
    {
        var boardId = request.BoardId ?? "default";
        var game = _gameService.GetGameForBoard(boardId);
        var previousTurn = game?.CurrentTurn ?? new Turn();
        var dartCount = previousTurn.Darts?.Count ?? 0;
        
        _gameService.ClearBoard(boardId);
        _dartDetect.ClearBoard(boardId);
        
        if (game != null && game.State == GameState.Finished)
        {
            await _hubContext.SendRebase(boardId);
            await _hubContext.SendBoardCleared(boardId);
        }
else if (game != null && (game.EngineState == EngineState.LegEnded || game.EngineState == EngineState.SetEnded))
        {
            // Leg/set just ended - don't advance turn, just clear board and notify UI
            _logger.LogInformation("Board cleared during LegEnded/SetEnded state - waiting for next-leg call");
            await _hubContext.SendRebase(boardId);
            await _hubContext.SendBoardCleared(boardId);
            return Ok(new { message = "Board cleared (leg ended)", turnAdvanced = false, dartCount, legEnded = true });
        }
        else if (game != null && previousTurn.IsBusted)
        {
            // Bust is active — record that board is cleared
            previousTurn.BustBoardCleared = true;
            _logger.LogInformation("Board cleared during bust on board {BoardId}", boardId);
            
            // Notify UI that board is cleared (for status text update)
            await _hubContext.Clients.Group($"board:{boardId}").SendAsync("BustBoardCleared", new
            {
                boardId,
                playerId = previousTurn.PlayerId
            });
            
            // If bust is also confirmed, advance turn now
            if (previousTurn.BustConfirmed)
            {
                _logger.LogInformation("Bust already confirmed, advancing turn");
                _gameService.NextTurn(game);
                await _hubContext.SendResumeDetection(boardId);
                var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
                await UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
                await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
                return Ok(new { message = "Board cleared", turnAdvanced = true, dartCount });
            }
            
            return Ok(new { message = "Board cleared during bust, waiting for confirm", turnAdvanced = false, dartCount });
        }
        else if (game != null)
        {
            // Normal (non-bust) board clear — advance turn
            _gameService.NextTurn(game);
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        }
        else
        {
            await _hubContext.SendRebase(boardId);
            await _hubContext.SendBoardCleared(boardId);
        }
        
        return Ok(new { message = "Board cleared", turnAdvanced = game != null && !previousTurn.IsBusted, dartCount });
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
            .Where(g => g.GameState == 2)
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
                g.startedAt,
                g.endedAt,
                g.durationSeconds,
                player1 = players.ElementAtOrDefault(0) is var p1 && p1 != null ? new { name = p1.Name, score = p1.IsWinner ? 3 : (3 - 1), isWinner = p1.IsWinner } : null,
                player2 = players.ElementAtOrDefault(1) is var p2 && p2 != null ? new { name = p2.Name, score = p2.IsWinner ? 3 : (3 - 1), isWinner = p2.IsWinner } : null,
                winner = players.FirstOrDefault(p => p.IsWinner)?.Name
            };
        });

        var gamesToday = await _db.Games.CountAsync(g => g.StartedAt >= today);
        var totalGames = await _db.Games.CountAsync();

        return Ok(new { games = result, gamesToday, totalGames });
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
            board.CalibrationData = request.CalibrationData;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Calibration updated" });
    }

    /// <summary>
    /// Pre-flight check
    /// </summary>
    [HttpGet("preflight/{boardId}")]
    public async Task<ActionResult<PreflightResult>> PreflightCheck(string boardId)
    {
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId && b.IsActive);
        
        var cameras = await _db.Cameras
            .Where(c => c.BoardId == boardId && c.IsActive)
            .Select(c => new { c.CameraId, c.IsCalibrated, c.CalibrationQuality })
            .ToListAsync();
        
        var sensorConnected = false;
        try
        {
            using var sensorClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var sensorResp = await sensorClient.GetAsync("http://127.0.0.1:8001/status");
            if (sensorResp.IsSuccessStatusCode)
            {
                var json = await sensorResp.Content.ReadAsStringAsync();
                sensorConnected = json.Contains("\"ready\"") && json.Contains("true");
            }
        }
        catch { }
        
        var issues = new List<PreflightIssue>();
        
        if (board == null)
            issues.Add(new PreflightIssue { Code = "BOARD_NOT_FOUND", Message = "Board not registered", Severity = "error" });
        
        if (cameras.Count == 0)
        {
            issues.Add(new PreflightIssue { Code = "NO_CAMERAS", Message = "No cameras registered", Severity = "error" });
        }
        else
        {
            var uncalibrated = cameras.Where(c => !c.IsCalibrated).Select(c => c.CameraId).ToList();
            if (uncalibrated.Any())
                issues.Add(new PreflightIssue { Code = "NOT_CALIBRATED", Message = $"Cameras not calibrated: {string.Join(", ", uncalibrated)}", Severity = "error", Details = uncalibrated });
        }
        
        if (!sensorConnected)
            issues.Add(new PreflightIssue { Code = "SENSOR_DISCONNECTED", Message = "Sensor not connected", Severity = "error" });
        
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
    /// Create a new game
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Game>> CreateGame([FromBody] CreateGameRequest request)
    {
        try
        {
            var boardId = request.BoardId ?? "default";
            
            var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId && b.IsActive);
            if (board == null)
            {
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
                    return NotFound(new { error = "Board not found", code = "BOARD_NOT_FOUND", boardId });
                }
            }
            
            var cameras = await _db.Cameras.Where(c => c.BoardId == boardId && c.IsActive).ToListAsync();
            if (cameras.Count == 0)
                return BadRequest(new { error = "No cameras registered", code = "NO_CAMERAS", message = "Please register cameras in Settings before starting a game", boardId });
            
            var uncalibrated = cameras.Where(c => !c.IsCalibrated).Select(c => c.CameraId).ToList();
            if (uncalibrated.Any())
                return BadRequest(new { error = "Cameras not calibrated", code = "NOT_CALIBRATED", uncalibratedCameras = uncalibrated, boardId });
            
            var sensorUp = false;
            try
            {
                using var sc = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await sc.GetAsync("http://127.0.0.1:8001/status");
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    sensorUp = json.Contains("\"ready\"") && json.Contains("true");
                }
            }
            catch { }
            if (!sensorUp)
                return BadRequest(new { error = "Sensor not connected", code = "SENSOR_DISCONNECTED", boardId });
            
            _dartDetect.InitBoard(boardId);

            Game game;
            // Cricket games
            if (request.Mode == GameMode.Cricket || request.Mode == GameMode.CricketCutthroat)
            {
                game = _cricketEngine.StartMatch(request.Mode, request.PlayerNames, boardId, request.BestOf);
                _gameService.RegisterGame(game, boardId);
            }
            // Use full MatchConfig when X01-specific options are provided
            else if (request.StartingScore > 0 || request.DoubleIn || request.MasterOut || request.SetsEnabled ||
                request.Mode == GameMode.X01)
            {
                var config = new MatchConfig
                {
                    StartingScore = request.StartingScore > 0 ? request.StartingScore 
                        : (request.Mode == GameMode.Game301 ? 301 : request.Mode == GameMode.Debug20 ? 20 : 501),
                    DoubleIn = request.DoubleIn,
                    DoubleOut = request.RequireDoubleOut,
                    MasterOut = request.MasterOut,
                    SetsEnabled = request.SetsEnabled,
                    SetsToWin = request.SetsToWin,
                    LegsPerSet = request.LegsPerSet,
                    LegsToWin = (request.BestOf / 2) + 1,
                    StartingPlayerRule = Enum.TryParse<StartingPlayerRule>(request.StartingPlayerRule, true, out var spr) 
                        ? spr : Models.StartingPlayerRule.Alternate
                };
                game = _gameService.CreateGameWithConfig(boardId, config, request.PlayerNames);
            }
            else
            {
                game = _gameService.CreateGame(boardId, request.Mode, request.PlayerNames, request.BestOf, request.RequireDoubleOut);
            }
            
            await _hubContext.SendGameStarted(boardId, game);
            _logger.LogInformation("Game {GameId} created on board {BoardId}", game.Id, boardId);
            
            return Ok(game);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message, code = "GAME_ERROR" });
        }
    }

    /// <summary>
    /// Delete a game and all associated data
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteGame(string id)
    {
        if (!Guid.TryParse(id, out var gameId))
            return BadRequest(new { error = "Invalid game ID" });

        var game = await _db.Games.FirstOrDefaultAsync(g => g.GameId == gameId);
        if (game == null) return NotFound(new { error = "Game not found" });

        // Delete throws via game players
        var gamePlayerIds = await _db.GamePlayers.Where(gp => gp.GameId == gameId).Select(gp => gp.GamePlayerId).ToListAsync();
        var throws = _db.Throws.Where(t => gamePlayerIds.Contains(t.GamePlayerId));
        _db.Throws.RemoveRange(throws);

        var gamePlayers = _db.GamePlayers.Where(gp => gp.GameId == gameId);
        _db.GamePlayers.RemoveRange(gamePlayers);

        // DartLocation uses string GameId
        var gameIdStr = id;
        var dartLocations = _db.DartLocations.Where(dl => dl.GameId == gameIdStr);
        _db.DartLocations.RemoveRange(dartLocations);

        _db.Games.Remove(game);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Game {GameId} deleted", gameId);
        return Ok(new { message = "Game deleted", gameId });
    }

    [HttpGet("{id}")]
    public ActionResult<Game> GetGame(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        return Ok(game);
    }

    [HttpGet("board/{boardId}")]
    public ActionResult<Game> GetGameForBoard(string boardId)
    {
        var game = _gameService.GetGameForBoard(boardId);
        if (game == null) return NotFound(new { message = "No active game on this board" });
        return Ok(game);
    }

    [HttpPost("{id}/end")]
    public async Task<ActionResult> EndGame(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        _gameService.EndGame(id);
        await _hubContext.SendGameEnded(game.BoardId, game);
        return Ok(new { message = "Game ended" });
    }

    /// <summary>
    /// Advance to next player's turn
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
        
        var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
        _ = UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
        
        await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        return Ok(new { game });
    }

    /// <summary>
    /// Confirm bust and end turn
    /// </summary>
    [HttpPost("{id}/confirm-bust")]
    public async Task<ActionResult> ConfirmBust(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });
        
        if (game.CurrentTurn == null || (!game.CurrentTurn.IsBusted && !game.CurrentTurn.BustPending && game.PendingBusts.Count == 0))
            return BadRequest(new { error = "Current turn is not busted" });
        
        var turn = game.CurrentTurn!;
        _gameService.ConfirmBust(game);
        turn.BustConfirmed = true;
        _logger.LogInformation("Bust confirmed on board {BoardId}", game.BoardId);
        
        // Check if board is already cleared — if so, advance turn now
        if (turn.BustBoardCleared)
        {
            _logger.LogInformation("Board already cleared, advancing turn and resuming sensor");
            var previousTurn = turn;
            _gameService.NextTurn(game);
            await _hubContext.SendResumeDetection(game.BoardId);
            
            var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
            await UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
            
            return Ok(new { game, turnAdvanced = true });
        }
        
        // Board not yet cleared — notify UI, wait for board clear event
        await _hubContext.Clients.Group($"board:{game.BoardId}").SendAsync("BustConfirmedWaitingForClear", new
        {
            playerId = turn.PlayerId,
            message = "Bust confirmed — pull darts to continue"
        });
        
        return Ok(new { game, turnAdvanced = false, waitingForBoardClear = true });
    }

    /// <summary>
    /// Override a pending bust with a corrected dart
    /// </summary>
    [HttpPost("{id}/override-bust")]
    public async Task<ActionResult> OverrideBust(string id, [FromBody] OverrideBustRequest request)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (!game.IsX01Engine) return BadRequest(new { error = "Not an X01 game" });
        
        var pendingBust = game.PendingBusts.FirstOrDefault(b => b.Id == request.PendingBustId);
        if (pendingBust == null) return BadRequest(new { error = "No pending bust found" });
        
        var correctedDart = new DartThrow
        {
            Segment = request.Segment,
            Multiplier = request.Multiplier,
            Zone = GetZoneName(request.Segment, request.Multiplier),
            Score = request.Segment * request.Multiplier,
            Confidence = 1.0
        };
        
        var result = _x01Engine.OverrideBustWithCorrectedDart(game, request.PendingBustId, correctedDart);
        
        await _hubContext.SendDartThrown(game.BoardId, correctedDart, game);
        
        if (game.State == GameState.Finished)
            await _hubContext.SendGameEnded(game.BoardId, game);
        
        return Ok(new { result = result.Type.ToString(), game });
    }

    /// <summary>
    /// Start next leg after a leg has been won
    /// </summary>
    [HttpPost("{id}/next-leg")]
    public async Task<ActionResult> NextLeg(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        
        _gameService.StartNextLeg(game);
        
        await _hubContext.Clients.Group($"board:{game.BoardId}").SendAsync("LegStarted", new
        {
            game.CurrentLeg,
            currentPlayer = game.CurrentPlayer?.Name,
            game
        });
        
        return Ok(new { game });
    }

    /// <summary>
    /// Clear the board
    /// </summary>
    [HttpPost("board/{boardId}/clear")]
    public async Task<ActionResult> ClearBoard(string boardId)
    {
        var game = _gameService.GetGameForBoard(boardId);
        var turnWasComplete = game?.CurrentTurn?.IsComplete == true;
        var previousTurn = game?.CurrentTurn ?? new Turn();
        
        _gameService.ClearBoard(boardId);
        
        if (turnWasComplete && game != null)
        {
            _gameService.NextTurn(game);
            var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
            await UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
        }
        
        await _hubContext.SendRebase(boardId);
        
        if (turnWasComplete && game != null)
        {
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        }
        else
        {
            await _hubContext.SendBoardCleared(boardId);
        }
        
        return Ok(new { message = "Board cleared", turnAdvanced = turnWasComplete });
    }

    /// <summary>
    /// Manual dart entry
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
            Confidence = 1.0
        };

        var dartResult = _gameService.ApplyManualDart(game, dart);
        await _hubContext.SendDartThrown(game.BoardId, dart, game);
        
        if (game.State == GameState.Finished)
            await _hubContext.SendGameEnded(game.BoardId, game);
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

        var wasBusted = game.CurrentTurn.IsBusted;
        var boardAlreadyCleared = game.CurrentTurn.BustBoardCleared;
        var oldDart = game.CurrentTurn.Darts[request.DartIndex];
        
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

        // Route through X01 engine for X01 games, fallback to GameService for others
        DartResult correctionResult = null;
        if (game.IsX01Engine)
        {
            var currentPlayer = game.CurrentPlayer;
            if (currentPlayer != null)
                correctionResult = _x01Engine.CorrectDart(game, currentPlayer.Id, request.DartIndex, newDart);
        }
        else
        {
            _gameService.CorrectDart(game, request.DartIndex, newDart);
        }

        if (_benchmark.IsEnabled)
        {
            var corrPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex)?.Name ?? "player";
            _ = Task.Run(() => _benchmark.SaveCorrectionAsync(
                game.BoardId, game.Id, game.CurrentRound, corrPlayer, request.DartIndex + 1, oldDart, newDart));
        }

        _ = RecordBenchmarkCorrection(game, request.DartIndex, oldDart, newDart);

        await _hubContext.SendDartThrown(game.BoardId, newDart, game);

        // Check if correction resulted in checkout or match end
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }
        else if (correctionResult?.Type == DartResultType.LegWon || game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }
        else if (wasBusted)
        {
            // Was busted before correction — check if still busted
            if (game.CurrentTurn!.IsBusted)
            {
                // Still busted after correction
                await _hubContext.Clients.Group($"board:{game.BoardId}").SendAsync("BustStillActive", new
                {
                    playerId = game.CurrentTurn.PlayerId,
                    message = "Still busted after correction"
                });
            }
            else
            {
                // No longer busted! Clear bust flags
                game.CurrentTurn.BustPending = false;
                game.CurrentTurn.BustConfirmed = false;
                game.PendingBusts.Clear();
                game.EngineState = EngineState.InTurnAwaitingThrow;
                
                _logger.LogInformation("Correction cleared bust on board {BoardId}", game.BoardId);
                
                await _hubContext.Clients.Group($"board:{game.BoardId}").SendAsync("BustCancelled", new
                {
                    playerId = game.CurrentTurn.PlayerId,
                    boardCleared = boardAlreadyCleared,
                    message = "Bust cleared by correction"
                });
                
                // If board was already cleared (scenario 6), rebase sensor baselines
                if (boardAlreadyCleared)
                {
                    game.CurrentTurn.BustBoardCleared = false;
                    await _hubContext.SendRebase(game.BoardId);
                }
            }
        }

        return Ok(new ThrowResult { NewDart = newDart, Game = game });
    }

    /// <summary>
    /// Remove a false dart from the current turn
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

        // Tag phantom dart in benchmark data
        try
        {
            var bmBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DartDetector", "benchmark");
            // Find the game folder
            foreach (var boardDir in Directory.GetDirectories(bmBase))
            {
                var gameDir = Path.Combine(boardDir, id);
                if (!Directory.Exists(gameDir)) continue;
                
                // Find dart folders matching this dart index in current round
                var player = game.CurrentPlayer?.Name ?? "player";
                var roundDir = $"round_{game.CurrentRound}_{player}";
                var dartDir = Path.Combine(gameDir, roundDir, $"dart_{request.DartIndex + 1}");
                if (Directory.Exists(dartDir))
                {
                    var phantomDir = dartDir + "_phantom";
                    if (!Directory.Exists(phantomDir))
                    {
                        Directory.Move(dartDir, phantomDir);
                        _logger.LogInformation("[PHANTOM] Tagged {Dir} as phantom", phantomDir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to tag phantom dart");
        }

        try
        {
            using var httpClient = new HttpClient();
            var excludePayload = new { game_id = id, dart_index = request.DartIndex, reason = "false_detection_removed" };
            var json = System.Text.Json.JsonSerializer.Serialize(excludePayload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var dartDetectUrl = Environment.GetEnvironmentVariable("DARTDETECT_URL") ?? "http://localhost:8000";
            await httpClient.PostAsync($"{dartDetectUrl}/v1/benchmark/exclude-dart", content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to exclude dart from benchmark");
        }

        await _hubContext.SendDartRemoved(game.BoardId, removedDart, game);
        return Ok(new RemoveDartResult { RemovedDart = removedDart, Game = game });
    }

    /// <summary>
    /// Benchmark detect endpoint
    /// </summary>
    [HttpPost("benchmark/detect")]
    public async Task<ActionResult> BenchmarkDetect([FromBody] DetectRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var requestId = request.RequestId ?? Guid.NewGuid().ToString()[..8];

        var images = request.Images?.Select(i => new CameraImageDto { CameraId = i.CameraId, Image = i.Image }).ToList() ?? new List<CameraImageDto>();
        var beforeImages = request.BeforeImages?.Select(i => new CameraImageDto { CameraId = i.CameraId, Image = i.Image }).ToList();

        var boardId = request.BoardId ?? "default";
        var detectResult = await _dartDetect.DetectAsync(images, boardId, 1, beforeImages);

        if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
            return Ok(new { message = "No darts detected", darts = new List<object>(), processingMs = sw.ElapsedMilliseconds, requestId });

        var tip = detectResult.Tips.OrderByDescending(t => t.Confidence).First();
        return Ok(new {
            message = "Dart detected",
            darts = new[] { new { tip.Zone, tip.Score, tip.Segment, tip.Multiplier, tip.Confidence } },
            processingMs = sw.ElapsedMilliseconds,
            requestId,
            isNative = _dartDetect is NativeDartDetectService
        });
    }

    // === Private helpers ===

    private static string GetZoneName(int segment, int multiplier)
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

    private async Task RecordBenchmarkCorrection(Game game, int dartIndex, DartThrow oldDart, DartThrow newDart)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            var payload = new
            {
                dart_number = dartIndex + 1,
                game_id = game.Id,
                original_segment = oldDart.Segment,
                original_multiplier = oldDart.Multiplier,
                corrected_segment = newDart.Segment,
                corrected_multiplier = newDart.Multiplier
            };
            await client.PostAsJsonAsync("http://127.0.0.1:8000/v1/benchmark/correction", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to record benchmark correction: {Error}", ex.Message);
        }
    }

    private async Task UpdateBenchmarkContext(string boardId, string gameId, int round, string? playerName)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            var payload = new { board_id = boardId, game_id = gameId, round_num = round, player_name = playerName ?? "player" };
            await client.PostAsJsonAsync("http://127.0.0.1:8000/v1/benchmark/context", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to update benchmark context: {Error}", ex.Message);
        }
    }
}

// === Request/Response DTOs ===

public class DetectRequest
{
    public string? BoardId { get; set; }
    public List<ImagePayload>? Images { get; set; }
    public List<ImagePayload>? BeforeImages { get; set; }
    public List<List<ImagePayload>>? MultiFrameImages { get; set; }
    public string? RequestId { get; set; }
    /// <summary>Camera hardware settings at time of capture (from DartSensor)</summary>
    public Dictionary<string, Dictionary<string, object>>? CameraSettings { get; set; }
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
    // New X01 config options
    public bool DoubleIn { get; set; } = false;
    public bool MasterOut { get; set; } = false;
    public bool SetsEnabled { get; set; } = false;
    public int SetsToWin { get; set; } = 3;
    public int LegsPerSet { get; set; } = 3;
    public int StartingScore { get; set; } = 0;  // 0 = use mode default
    public string StartingPlayerRule { get; set; } = "Alternate";
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

public class OverrideBustRequest
{
    public string PendingBustId { get; set; } = string.Empty;
    public int Segment { get; set; }
    public int Multiplier { get; set; } = 1;
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

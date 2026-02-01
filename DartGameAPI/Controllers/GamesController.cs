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
    private readonly IHubContext<GameHub> _hubContext;
    private readonly DartsMobDbContext _db;
    private readonly ILogger<GamesController> _logger;

    public GamesController(GameService gameService, IHubContext<GameHub> hubContext, DartsMobDbContext db, ILogger<GamesController> logger)
    {
        _gameService = gameService;
        _hubContext = hubContext;
        _db = db;
        _logger = logger;
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
    /// Create a new game
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Game>> CreateGame([FromBody] CreateGameRequest request)
    {
        try
        {
            var game = _gameService.CreateGame(request.BoardId, request.Mode, request.PlayerNames, request.BestOf);
            
            // Notify connected clients
            await _hubContext.SendGameStarted(request.BoardId, game);
            
            return Ok(game);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
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
    /// Process a dart throw (called by DartSensor when motion detected)
    /// </summary>
    [HttpPost("{id}/throw")]
    public async Task<ActionResult<ThrowResult>> ProcessThrow(string id, [FromBody] ThrowRequest request)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        if (game.State != GameState.InProgress)
            return BadRequest(new { error = "Game is not in progress" });

        var dart = await _gameService.ProcessThrowAsync(game.BoardId, request.Images);
        
        if (dart == null)
        {
            return Ok(new ThrowResult { NewDart = null, Game = game });
        }

        // Notify connected clients
        await _hubContext.SendDartThrown(game.BoardId, dart, game);
        
        // Check if game ended
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }

        return Ok(new ThrowResult { NewDart = dart, Game = game });
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
        
        // Save current turn info before advancing
        var previousTurn = game.CurrentTurn ?? new Turn();
        
        _gameService.NextTurn(game);
        
        // Notify connected clients that turn ended
        await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
        
        return Ok(new { game = game });
    }

    /// <summary>
    /// Clear the board (darts removed)
    /// </summary>
    [HttpPost("board/{boardId}/clear")]
    public async Task<ActionResult> ClearBoard(string boardId)
    {
        _gameService.ClearBoard(boardId);
        await _hubContext.SendBoardCleared(boardId);
        return Ok(new { message = "Board cleared" });
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

        // Create dart throw directly
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

        // Apply to game
        _gameService.ApplyManualDart(game, dart);
        
        // Notify connected clients
        await _hubContext.SendDartThrown(game.BoardId, dart, game);
        
        // Check if game ended
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

        // Get the old dart and calculate score difference
        var oldDart = game.CurrentTurn.Darts[request.DartIndex];
        var oldScore = oldDart.Score;
        
        // Create corrected dart
        var newDart = new DartThrow
        {
            Index = request.DartIndex,
            Segment = request.Segment,
            Multiplier = request.Multiplier,
            Zone = GetZoneName(request.Segment, request.Multiplier),
            Score = request.Segment * request.Multiplier,
            XMm = oldDart.XMm,
            YMm = oldDart.YMm,
            Confidence = 1.0  // Manual correction = 100% confidence
        };

        // Apply correction
        _gameService.CorrectDart(game, request.DartIndex, newDart);
        
        _logger.LogInformation("Corrected dart {Index}: {OldZone}={OldScore} -> {NewZone}={NewScore}", 
            request.DartIndex, oldDart.Zone, oldScore, newDart.Zone, newDart.Score);

        // Notify connected clients
        await _hubContext.SendDartThrown(game.BoardId, newDart, game);
        
        // Check if game ended (unlikely from correction but possible)
        if (game.State == GameState.Finished)
        {
            await _hubContext.SendGameEnded(game.BoardId, game);
        }

        return Ok(new ThrowResult { NewDart = newDart, Game = game });
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

public class CreateGameRequest
{
    public string BoardId { get; set; } = "default";
    public GameMode Mode { get; set; } = GameMode.Practice;
    public List<string> PlayerNames { get; set; } = new();
    public int BestOf { get; set; } = 5;  // Best of 5 legs (first to 3)
}

public class ThrowRequest
{
    public List<CameraImage> Images { get; set; } = new();
}

public class ManualThrowRequest
{
    public int Segment { get; set; }  // 1-20, or 25 for bull
    public int Multiplier { get; set; } = 1;  // 1=single, 2=double, 3=triple
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

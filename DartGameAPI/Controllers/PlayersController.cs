using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Data;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlayersController : ControllerBase
{
    private readonly DartsMobDbContext _db;
    private readonly ILogger<PlayersController> _logger;

    public PlayersController(DartsMobDbContext db, ILogger<PlayersController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Get all active players
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlayerDto>>> GetPlayers()
    {
        var players = await _db.Players
            .Where(p => p.IsActive)
            .OrderBy(p => p.Nickname)
            .Select(p => new PlayerDto
            {
                PlayerId = p.PlayerId,
                Nickname = p.Nickname,
                AvatarUrl = p.AvatarUrl,
                CreatedAt = p.CreatedAt
            })
            .ToListAsync();

        return Ok(players);
    }

    /// <summary>
    /// Get a player by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<PlayerDto>> GetPlayer(Guid id)
    {
        var player = await _db.Players
            .Where(p => p.PlayerId == id && p.IsActive)
            .Select(p => new PlayerDto
            {
                PlayerId = p.PlayerId,
                Nickname = p.Nickname,
                Email = p.Email,
                AvatarUrl = p.AvatarUrl,
                CreatedAt = p.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (player == null) return NotFound();
        return Ok(player);
    }

    /// <summary>
    /// Create a new player
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PlayerDto>> CreatePlayer([FromBody] CreatePlayerRequest request)
    {
        // Check if nickname exists
        var exists = await _db.Players.AnyAsync(p => p.Nickname == request.Nickname && p.IsActive);
        if (exists)
        {
            return BadRequest(new { error = "Nickname already taken" });
        }

        var player = new PlayerEntity
        {
            PlayerId = Guid.NewGuid(),
            Nickname = request.Nickname,
            Email = request.Email,
            AvatarUrl = request.AvatarUrl,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _db.Players.Add(player);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created player {Nickname} ({PlayerId})", player.Nickname, player.PlayerId);

        return CreatedAtAction(nameof(GetPlayer), new { id = player.PlayerId }, new PlayerDto
        {
            PlayerId = player.PlayerId,
            Nickname = player.Nickname,
            Email = player.Email,
            AvatarUrl = player.AvatarUrl,
            CreatedAt = player.CreatedAt
        });
    }

    /// <summary>
    /// Update a player
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<PlayerDto>> UpdatePlayer(Guid id, [FromBody] UpdatePlayerRequest request)
    {
        var player = await _db.Players.FirstOrDefaultAsync(p => p.PlayerId == id && p.IsActive);
        if (player == null) return NotFound();

        // Check nickname uniqueness if changing
        if (!string.IsNullOrEmpty(request.Nickname) && request.Nickname != player.Nickname)
        {
            var exists = await _db.Players.AnyAsync(p => p.Nickname == request.Nickname && p.IsActive);
            if (exists)
            {
                return BadRequest(new { error = "Nickname already taken" });
            }
            player.Nickname = request.Nickname;
        }

        if (request.Email != null) player.Email = request.Email;
        if (request.AvatarUrl != null) player.AvatarUrl = request.AvatarUrl;
        player.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new PlayerDto
        {
            PlayerId = player.PlayerId,
            Nickname = player.Nickname,
            Email = player.Email,
            AvatarUrl = player.AvatarUrl,
            CreatedAt = player.CreatedAt
        });
    }

    /// <summary>
    /// Delete a player (soft delete)
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeletePlayer(Guid id)
    {
        var player = await _db.Players.FirstOrDefaultAsync(p => p.PlayerId == id && p.IsActive);
        if (player == null) return NotFound();

        player.IsActive = false;
        player.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted player {Nickname} ({PlayerId})", player.Nickname, player.PlayerId);
        return Ok(new { message = "Player deleted" });
    }

    /// <summary>
    /// Get player statistics
    /// </summary>
    [HttpGet("{id}/stats")]
    public async Task<ActionResult<PlayerStatsDto>> GetPlayerStats(Guid id)
    {
        var player = await _db.Players
            .Where(p => p.PlayerId == id && p.IsActive)
            .FirstOrDefaultAsync();

        if (player == null) return NotFound();

        // Get stats from game history
        var stats = await _db.GamePlayers
            .Where(gp => gp.PlayerId == id)
            .GroupBy(gp => gp.PlayerId)
            .Select(g => new
            {
                GamesPlayed = g.Count(),
                GamesWon = g.Count(gp => gp.IsWinner),
                TotalDarts = g.Sum(gp => gp.DartsThrown),
                BestTurn = g.Max(gp => gp.HighestTurn)
            })
            .FirstOrDefaultAsync();

        return Ok(new PlayerStatsDto
        {
            PlayerId = player.PlayerId,
            Nickname = player.Nickname,
            GamesPlayed = stats?.GamesPlayed ?? 0,
            GamesWon = stats?.GamesWon ?? 0,
            WinRate = stats != null && stats.GamesPlayed > 0 
                ? Math.Round((double)stats.GamesWon / stats.GamesPlayed * 100, 1) 
                : 0,
            TotalDartsThrown = stats?.TotalDarts ?? 0,
            BestTurnScore = stats?.BestTurn
        });
    }
}

// DTOs
public class PlayerDto
{
    public Guid PlayerId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreatePlayerRequest
{
    public string Nickname { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

public class UpdatePlayerRequest
{
    public string? Nickname { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

public class PlayerStatsDto
{
    public Guid PlayerId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public int GamesPlayed { get; set; }
    public int GamesWon { get; set; }
    public double WinRate { get; set; }
    public int TotalDartsThrown { get; set; }
    public int? BestTurnScore { get; set; }
}

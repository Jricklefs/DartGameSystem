using Microsoft.AspNetCore.Mvc;
using DartGameAPI.Services;
using DartGameAPI.Models;
using DartGameAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OnlineController : ControllerBase
{
    private readonly MatchmakingService _matchmaking;
    private readonly DartsMobDbContext _db;
    private readonly ILogger<OnlineController> _logger;

    public OnlineController(
        MatchmakingService matchmaking, 
        DartsMobDbContext db,
        ILogger<OnlineController> logger)
    {
        _matchmaking = matchmaking;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Register a new board
    /// </summary>
    [HttpPost("boards")]
    public async Task<IActionResult> RegisterBoard([FromBody] OnlineRegisterBoardRequest request)
    {
        var board = new RegisteredBoard
        {
            Name = request.Name,
            OwnerId = request.OwnerId,
            Location = request.Location,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Timezone = request.Timezone,
            IsPublic = request.IsPublic
        };

        _db.Set<RegisteredBoard>().Add(board);
        await _db.SaveChangesAsync();

        return Ok(new { board.Id, board.Name, Message = "Board registered successfully" });
    }

    /// <summary>
    /// Get player's boards
    /// </summary>
    [HttpGet("boards/{playerId}")]
    public async Task<IActionResult> GetPlayerBoards(Guid playerId)
    {
        var boards = await _db.Set<RegisteredBoard>()
            .Where(b => b.OwnerId == playerId)
            .Select(b => new { b.Id, b.Name, b.Location, b.IsPublic, b.LastOnlineAt })
            .ToListAsync();

        return Ok(boards);
    }

    /// <summary>
    /// Get online player count and map data
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetOnlineStatus()
    {
        var count = _matchmaking.GetOnlineCount();
        var mapPoints = _matchmaking.GetPlayerMapPoints();

        return Ok(new
        {
            OnlineCount = count,
            Players = mapPoints
        });
    }

    /// <summary>
    /// Get full player map for globe visualization
    /// </summary>
    [HttpGet("map")]
    public IActionResult GetPlayerMap()
    {
        var points = _matchmaking.GetPlayerMapPoints()
            .Select(p => new
            {
                lat = p.Latitude,
                lon = p.Longitude,
                status = p.Status.ToString().ToLower()
            });

        return Ok(new
        {
            total = _matchmaking.GetOnlineCount(),
            players = points
        });
    }

    /// <summary>
    /// Get friends list with online status
    /// </summary>
    [HttpGet("friends/{playerId}")]
    public async Task<IActionResult> GetFriends(Guid playerId)
    {
        var friends = await _matchmaking.GetFriendsWithStatus(playerId);
        return Ok(friends);
    }

    /// <summary>
    /// Send friend request
    /// </summary>
    [HttpPost("friends/request")]
    public async Task<IActionResult> SendFriendRequest([FromBody] FriendRequestDto request)
    {
        var result = await _matchmaking.SendFriendRequest(request.RequesterId, request.AddresseeId);
        return result ? Ok(new { success = true }) : BadRequest("Already friends or pending");
    }

    /// <summary>
    /// Accept friend request
    /// </summary>
    [HttpPost("friends/accept")]
    public async Task<IActionResult> AcceptFriendRequest([FromBody] AcceptFriendDto request)
    {
        var result = await _matchmaking.AcceptFriendRequest(request.FriendshipId, request.PlayerId);
        return result ? Ok(new { success = true }) : BadRequest("Request not found");
    }

    /// <summary>
    /// Get pending friend requests
    /// </summary>
    [HttpGet("friends/pending/{playerId}")]
    public async Task<IActionResult> GetPendingRequests(Guid playerId)
    {
        var pending = await _db.Set<Friendship>()
            .Where(f => f.AddresseeId == playerId && f.Status == FriendshipStatus.Pending)
            .Include(f => f.Requester)
            .Select(f => new { f.Id, RequesterId = f.RequesterId, RequesterName = f.Requester!.Name, f.CreatedAt })
            .ToListAsync();

        return Ok(pending);
    }

    /// <summary>
    /// Get player rating/stats
    /// </summary>
    [HttpGet("rating/{playerId}")]
    public async Task<IActionResult> GetPlayerRating(Guid playerId, [FromQuery] string? gameMode = null)
    {
        var query = _db.Set<PlayerRating>().Where(r => r.PlayerId == playerId);
        
        if (!string.IsNullOrEmpty(gameMode))
            query = query.Where(r => r.GameMode == gameMode);

        var ratings = await query.ToListAsync();
        return Ok(ratings);
    }

    /// <summary>
    /// Set availability schedule
    /// </summary>
    [HttpPost("availability")]
    public async Task<IActionResult> SetAvailability([FromBody] AvailabilityDto request)
    {
        var availability = new Availability
        {
            PlayerId = request.PlayerId,
            DayOfWeek = request.DayOfWeek,
            SpecificDate = request.SpecificDate,
            StartTime = TimeSpan.Parse(request.StartTime),
            EndTime = TimeSpan.Parse(request.EndTime),
            Timezone = request.Timezone,
            IsRecurring = request.IsRecurring
        };

        _db.Set<Availability>().Add(availability);
        await _db.SaveChangesAsync();

        return Ok(new { availability.Id });
    }

    /// <summary>
    /// Get player availability
    /// </summary>
    [HttpGet("availability/{playerId}")]
    public async Task<IActionResult> GetAvailability(Guid playerId)
    {
        var availability = await _db.Set<Availability>()
            .Where(a => a.PlayerId == playerId)
            .OrderBy(a => a.DayOfWeek)
            .ThenBy(a => a.StartTime)
            .ToListAsync();

        return Ok(availability);
    }

    /// <summary>
    /// Delete availability entry
    /// </summary>
    [HttpDelete("availability/{id}")]
    public async Task<IActionResult> DeleteAvailability(Guid id)
    {
        var entry = await _db.Set<Availability>().FindAsync(id);
        if (entry == null) return NotFound();

        _db.Set<Availability>().Remove(entry);
        await _db.SaveChangesAsync();

        return Ok();
    }

    /// <summary>
    /// Find players available now
    /// </summary>
    [HttpGet("available-now")]
    public async Task<IActionResult> GetAvailableNow([FromQuery] string? timezone = null)
    {
        var now = DateTime.UtcNow;
        var currentDay = now.DayOfWeek;
        var currentTime = now.TimeOfDay;

        var availablePlayerIds = await _db.Set<Availability>()
            .Where(a => 
                (a.IsRecurring && a.DayOfWeek == currentDay) ||
                (!a.IsRecurring && a.SpecificDate.HasValue && a.SpecificDate.Value.Date == now.Date))
            .Where(a => a.StartTime <= currentTime && a.EndTime >= currentTime)
            .Select(a => a.PlayerId)
            .Distinct()
            .ToListAsync();

        // Cross-reference with online players
        var onlinePlayers = _matchmaking.GetOnlinePlayers()
            .Where(p => availablePlayerIds.Contains(p.PlayerId))
            .Select(p => new { p.PlayerId, p.Status });

        return Ok(onlinePlayers);
    }
}

#region DTOs

public class OnlineRegisterBoardRequest
{
    public string Name { get; set; } = "";
    public Guid OwnerId { get; set; }
    public string? Location { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Timezone { get; set; }
    public bool IsPublic { get; set; } = true;
}

public class FriendRequestDto
{
    public Guid RequesterId { get; set; }
    public Guid AddresseeId { get; set; }
}

public class AcceptFriendDto
{
    public Guid FriendshipId { get; set; }
    public Guid PlayerId { get; set; }
}

public class AvailabilityDto
{
    public Guid PlayerId { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public DateTime? SpecificDate { get; set; }
    public string StartTime { get; set; } = "18:00";
    public string EndTime { get; set; } = "22:00";
    public string? Timezone { get; set; }
    public bool IsRecurring { get; set; } = true;
}

#endregion

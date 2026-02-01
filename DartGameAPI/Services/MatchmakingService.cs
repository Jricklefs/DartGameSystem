using DartGameAPI.Data;
using Microsoft.AspNetCore.SignalR;
using DartGameAPI.Hubs;
using DartGameAPI.Models;
using DartGameAPI.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace DartGameAPI.Services;

/// <summary>
/// Matchmaking service - handles queue, skill matching, and friend status
/// </summary>
public class MatchmakingService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<OnlineGameHub> _hubContext;
    private readonly ILogger<MatchmakingService> _logger;
    
    // In-memory queues for fast matching
    private readonly ConcurrentDictionary<Guid, MatchmakingEntry> _queue = new();
    private readonly ConcurrentDictionary<Guid, OnlineSession> _onlinePlayers = new();
    private readonly Timer _matchmakingTimer;

    public MatchmakingService(
        IServiceScopeFactory scopeFactory, 
        IHubContext<OnlineGameHub> hubContext,
        ILogger<MatchmakingService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
        
        // Run matchmaking every 5 seconds
        _matchmakingTimer = new Timer(ProcessMatchmaking, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    #region Online Status

    public async Task<OnlineSession> PlayerConnected(Guid playerId, string connectionId, Guid? boardId = null, double? lat = null, double? lon = null)
    {
        var session = new OnlineSession
        {
            PlayerId = playerId,
            ConnectionId = connectionId,
            BoardId = boardId,
            Latitude = lat,
            Longitude = lon,
            Status = PlayerStatus.Online,
            ConnectedAt = DateTime.UtcNow
        };

        _onlinePlayers[playerId] = session;

        // Notify friends
        await NotifyFriendsOfStatusChange(playerId, PlayerStatus.Online);

        _logger.LogInformation("Player {PlayerId} connected from {Lat},{Lon}", playerId, lat, lon);
        return session;
    }

    public async Task PlayerDisconnected(Guid playerId)
    {
        if (_onlinePlayers.TryRemove(playerId, out var session))
        {
            // Remove from matchmaking queue
            _queue.TryRemove(playerId, out _);
            
            // Notify friends
            await NotifyFriendsOfStatusChange(playerId, PlayerStatus.Offline);
            
            _logger.LogInformation("Player {PlayerId} disconnected", playerId);
        }
    }

    public void UpdateHeartbeat(Guid playerId)
    {
        if (_onlinePlayers.TryGetValue(playerId, out var session))
        {
            session.LastHeartbeat = DateTime.UtcNow;
        }
    }

    public IEnumerable<OnlineSession> GetOnlinePlayers() => _onlinePlayers.Values;

    public int GetOnlineCount() => _onlinePlayers.Count;

    #endregion

    #region Friends

    public async Task<List<PlayerWithStatus>> GetFriendsWithStatus(Guid playerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

        var friendships = await db.Set<Friendship>()
            .Where(f => (f.RequesterId == playerId || f.AddresseeId == playerId) 
                        && f.Status == FriendshipStatus.Accepted)
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .ToListAsync();

        var friends = new List<PlayerWithStatus>();
        
        foreach (var f in friendships)
        {
            var friendId = f.RequesterId == playerId ? f.AddresseeId : f.RequesterId;
            var friend = f.RequesterId == playerId ? f.Addressee : f.Requester;
            
            if (friend == null) continue;

            var isOnline = _onlinePlayers.TryGetValue(friendId, out var session);
            
            friends.Add(new PlayerWithStatus
            {
                PlayerId = friendId,
                Name = friend.Name,
                Status = isOnline ? session!.Status : PlayerStatus.Offline,
                LastOnline = isOnline ? null : session?.LastHeartbeat
            });
        }

        return friends.OrderByDescending(f => f.Status != PlayerStatus.Offline)
                      .ThenBy(f => f.Name)
                      .ToList();
    }

    public async Task<bool> SendFriendRequest(Guid requesterId, Guid addresseeId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

        // Check if already friends or pending
        var existing = await db.Set<Friendship>()
            .FirstOrDefaultAsync(f => 
                (f.RequesterId == requesterId && f.AddresseeId == addresseeId) ||
                (f.RequesterId == addresseeId && f.AddresseeId == requesterId));

        if (existing != null) return false;

        db.Set<Friendship>().Add(new Friendship
        {
            RequesterId = requesterId,
            AddresseeId = addresseeId,
            Status = FriendshipStatus.Pending
        });

        await db.SaveChangesAsync();

        // Notify addressee if online
        if (_onlinePlayers.TryGetValue(addresseeId, out var session))
        {
            await _hubContext.Clients.Client(session.ConnectionId)
                .SendAsync("FriendRequest", new { requesterId });
        }

        return true;
    }

    public async Task<bool> AcceptFriendRequest(Guid friendshipId, Guid playerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

        var friendship = await db.Set<Friendship>().FindAsync(friendshipId);
        if (friendship == null || friendship.AddresseeId != playerId) return false;

        friendship.Status = FriendshipStatus.Accepted;
        friendship.AcceptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Notify requester if online
        if (_onlinePlayers.TryGetValue(friendship.RequesterId, out var session))
        {
            await _hubContext.Clients.Client(session.ConnectionId)
                .SendAsync("FriendRequestAccepted", new { friendId = playerId });
        }

        return true;
    }

    private async Task NotifyFriendsOfStatusChange(Guid playerId, PlayerStatus status)
    {
        var friends = await GetFriendsWithStatus(playerId);
        
        foreach (var friend in friends.Where(f => f.Status != PlayerStatus.Offline))
        {
            if (_onlinePlayers.TryGetValue(friend.PlayerId, out var session))
            {
                await _hubContext.Clients.Client(session.ConnectionId)
                    .SendAsync("FriendStatusChanged", new { playerId, status });
            }
        }
    }

    #endregion

    #region Matchmaking

    public async Task<bool> JoinQueue(Guid playerId, Guid boardId, string gameMode, 
        MatchmakingPreference preference, int? minRating = null, int? maxRating = null, string? region = null)
    {
        if (!_onlinePlayers.TryGetValue(playerId, out var session))
            return false;

        var entry = new MatchmakingEntry
        {
            PlayerId = playerId,
            BoardId = boardId,
            GameMode = gameMode,
            Preference = preference,
            TargetRatingMin = minRating,
            TargetRatingMax = maxRating,
            PreferredRegion = region,
            ConnectionId = session.ConnectionId,
            QueuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _queue[playerId] = entry;
        session.Status = PlayerStatus.InQueue;

        await _hubContext.Clients.Client(session.ConnectionId)
            .SendAsync("JoinedQueue", new { position = _queue.Count });

        _logger.LogInformation("Player {PlayerId} joined queue for {GameMode}", playerId, gameMode);
        return true;
    }

    public async Task LeaveQueue(Guid playerId)
    {
        if (_queue.TryRemove(playerId, out _))
        {
            if (_onlinePlayers.TryGetValue(playerId, out var session))
            {
                session.Status = PlayerStatus.Online;
                await _hubContext.Clients.Client(session.ConnectionId)
                    .SendAsync("LeftQueue", new { });
            }
        }
    }

    private async void ProcessMatchmaking(object? state)
    {
        try
        {
            // Remove expired entries
            var expired = _queue.Where(kv => kv.Value.ExpiresAt < DateTime.UtcNow).ToList();
            foreach (var e in expired)
            {
                _queue.TryRemove(e.Key, out _);
            }

            // Group by game mode
            var byMode = _queue.Values.GroupBy(e => e.GameMode);

            foreach (var group in byMode)
            {
                var entries = group.OrderBy(e => e.QueuedAt).ToList();
                
                while (entries.Count >= 2)
                {
                    var player1 = entries[0];
                    MatchmakingEntry? player2 = null;

                    // Find compatible opponent
                    for (int i = 1; i < entries.Count; i++)
                    {
                        if (await AreCompatible(player1, entries[i]))
                        {
                            player2 = entries[i];
                            break;
                        }
                    }

                    if (player2 != null)
                    {
                        await CreateMatch(player1, player2);
                        entries.Remove(player1);
                        entries.Remove(player2);
                        _queue.TryRemove(player1.PlayerId, out _);
                        _queue.TryRemove(player2.PlayerId, out _);
                    }
                    else
                    {
                        entries.RemoveAt(0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in matchmaking processing");
        }
    }

    private async Task<bool> AreCompatible(MatchmakingEntry a, MatchmakingEntry b)
    {
        // Same game mode (already grouped)
        if (a.GameMode != b.GameMode) return false;

        // Check preference compatibility
        if (a.Preference == MatchmakingPreference.FriendsOnly || 
            b.Preference == MatchmakingPreference.FriendsOnly)
        {
            // Both must be friends
            var areFriends = await CheckFriendship(a.PlayerId, b.PlayerId);
            if (!areFriends) return false;
        }

        // Check skill preferences
        if (a.Preference == MatchmakingPreference.ChosenSkill || 
            b.Preference == MatchmakingPreference.ChosenSkill)
        {
            var ratingA = await GetPlayerRating(a.PlayerId, a.GameMode);
            var ratingB = await GetPlayerRating(b.PlayerId, b.GameMode);

            if (a.TargetRatingMin.HasValue && ratingB < a.TargetRatingMin) return false;
            if (a.TargetRatingMax.HasValue && ratingB > a.TargetRatingMax) return false;
            if (b.TargetRatingMin.HasValue && ratingA < b.TargetRatingMin) return false;
            if (b.TargetRatingMax.HasValue && ratingA > b.TargetRatingMax) return false;
        }

        if (a.Preference == MatchmakingPreference.SimilarSkill || 
            b.Preference == MatchmakingPreference.SimilarSkill)
        {
            var ratingA = await GetPlayerRating(a.PlayerId, a.GameMode);
            var ratingB = await GetPlayerRating(b.PlayerId, b.GameMode);
            if (Math.Abs(ratingA - ratingB) > 200) return false;
        }

        return true;
    }

    private async Task<int> GetPlayerRating(Guid playerId, string gameMode)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

        var rating = await db.Set<PlayerRating>()
            .FirstOrDefaultAsync(r => r.PlayerId == playerId && r.GameMode == gameMode);

        return rating?.Rating ?? 1200;
    }

    private async Task<bool> CheckFriendship(Guid a, Guid b)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();

        return await db.Set<Friendship>()
            .AnyAsync(f => 
                ((f.RequesterId == a && f.AddresseeId == b) || 
                 (f.RequesterId == b && f.AddresseeId == a)) &&
                f.Status == FriendshipStatus.Accepted);
    }

    private async Task CreateMatch(MatchmakingEntry player1, MatchmakingEntry player2)
    {
        var matchCode = GenerateMatchCode();

        // Update player statuses
        if (_onlinePlayers.TryGetValue(player1.PlayerId, out var s1))
            s1.Status = PlayerStatus.InMatch;
        if (_onlinePlayers.TryGetValue(player2.PlayerId, out var s2))
            s2.Status = PlayerStatus.InMatch;

        // Notify both players
        var matchData = new
        {
            MatchCode = matchCode,
            GameMode = player1.GameMode,
            Player1 = player1.PlayerId,
            Player2 = player2.PlayerId,
            BestOf = 5
        };

        if (!string.IsNullOrEmpty(player1.ConnectionId))
        {
            await _hubContext.Clients.Client(player1.ConnectionId)
                .SendAsync("MatchFound", matchData);
        }
        
        if (!string.IsNullOrEmpty(player2.ConnectionId))
        {
            await _hubContext.Clients.Client(player2.ConnectionId)
                .SendAsync("MatchFound", matchData);
        }

        _logger.LogInformation("Match created: {MatchCode} between {P1} and {P2}", 
            matchCode, player1.PlayerId, player2.PlayerId);
    }

    private static string GenerateMatchCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    #endregion

    #region Player Map

    public IEnumerable<PlayerMapPoint> GetPlayerMapPoints()
    {
        return _onlinePlayers.Values
            .Where(s => s.Latitude.HasValue && s.Longitude.HasValue)
            .Select(s => new PlayerMapPoint
            {
                PlayerId = s.PlayerId,
                Latitude = s.Latitude!.Value,
                Longitude = s.Longitude!.Value,
                Status = s.Status
            });
    }

    #endregion
}

#region DTOs

public class PlayerWithStatus
{
    public Guid PlayerId { get; set; }
    public string Name { get; set; } = "";
    public PlayerStatus Status { get; set; }
    public DateTime? LastOnline { get; set; }
}

public class PlayerMapPoint
{
    public Guid PlayerId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public PlayerStatus Status { get; set; }
}

#endregion

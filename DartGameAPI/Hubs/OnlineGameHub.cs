using Microsoft.AspNetCore.SignalR;
using DartGameAPI.Services;
using System.Collections.Concurrent;

namespace DartGameAPI.Hubs;

/// <summary>
/// SignalR hub for online multiplayer - connects players across the internet
/// </summary>
public class OnlineGameHub : Hub
{
    private readonly ILogger<OnlineGameHub> _logger;
    private readonly MatchmakingService _matchmaking;
    private static readonly ConcurrentDictionary<string, OnlineMatch> _matches = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToMatch = new();
    private static readonly ConcurrentDictionary<string, OnlinePlayer> _players = new();
    private static readonly ConcurrentDictionary<string, Guid> _connectionToPlayerId = new();

    public OnlineGameHub(ILogger<OnlineGameHub> logger, MatchmakingService matchmaking)
    {
        _logger = logger;
        _matchmaking = matchmaking;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Online player connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        _logger.LogInformation("Online player disconnected: {ConnectionId}", connectionId);

        // Notify matchmaking service
        if (_connectionToPlayerId.TryRemove(connectionId, out var playerId))
        {
            await _matchmaking.PlayerDisconnected(playerId);
        }

        // Clean up player from match
        if (_connectionToMatch.TryRemove(connectionId, out var matchCode))
        {
            if (_matches.TryGetValue(matchCode, out var match))
            {
                var player = match.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player != null)
                {
                    match.Players.Remove(player);
                    await Clients.Group($"match:{matchCode}").SendAsync("PlayerLeft", new
                    {
                        player.PlayerId,
                        player.DisplayName
                    });

                    // Clean up empty matches
                    if (match.Players.Count == 0)
                    {
                        _matches.TryRemove(matchCode, out _);
                    }
                }
            }
        }

        _players.TryRemove(connectionId, out _);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Register as an online player with optional geo-location
    /// </summary>
    public async Task Register(string displayName, string? playerId = null, Guid? boardId = null, double? lat = null, double? lon = null)
    {
        var pId = playerId != null ? Guid.Parse(playerId) : Guid.NewGuid();
        
        var player = new OnlinePlayer
        {
            ConnectionId = Context.ConnectionId,
            PlayerId = pId.ToString(),
            DisplayName = displayName,
            JoinedAt = DateTime.UtcNow
        };

        _players[Context.ConnectionId] = player;
        _connectionToPlayerId[Context.ConnectionId] = pId;
        
        // Register with matchmaking service
        await _matchmaking.PlayerConnected(pId, Context.ConnectionId, boardId, lat, lon);
        
        await Clients.Caller.SendAsync("Registered", new
        {
            player.PlayerId,
            player.DisplayName
        });

        _logger.LogInformation("Player registered: {DisplayName} ({PlayerId})", displayName, player.PlayerId);
    }

    /// <summary>
    /// Create a new online match and get a join code
    /// </summary>
    public async Task CreateMatch(string gameMode, int bestOf = 5)
    {
        if (!_players.TryGetValue(Context.ConnectionId, out var player))
        {
            await Clients.Caller.SendAsync("Error", "Please register first");
            return;
        }

        var matchCode = GenerateMatchCode();
        var match = new OnlineMatch
        {
            MatchCode = matchCode,
            GameMode = gameMode,
            BestOf = bestOf,
            HostConnectionId = Context.ConnectionId,
            CreatedAt = DateTime.UtcNow,
            State = MatchState.Waiting
        };

        player.IsHost = true;
        match.Players.Add(player);
        _matches[matchCode] = match;
        _connectionToMatch[Context.ConnectionId] = matchCode;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"match:{matchCode}");

        await Clients.Caller.SendAsync("MatchCreated", new
        {
            match.MatchCode,
            match.GameMode,
            match.BestOf,
            ShareLink = $"https://dartsmob.com/join/{matchCode}" // Future web join
        });

        _logger.LogInformation("Match created: {MatchCode} by {DisplayName}", matchCode, player.DisplayName);
    }

    /// <summary>
    /// Join an existing match by code
    /// </summary>
    public async Task JoinMatch(string matchCode)
    {
        matchCode = matchCode.ToUpper().Trim();

        if (!_players.TryGetValue(Context.ConnectionId, out var player))
        {
            await Clients.Caller.SendAsync("Error", "Please register first");
            return;
        }

        if (!_matches.TryGetValue(matchCode, out var match))
        {
            await Clients.Caller.SendAsync("Error", "Match not found");
            return;
        }

        if (match.State != MatchState.Waiting)
        {
            await Clients.Caller.SendAsync("Error", "Match already in progress");
            return;
        }

        if (match.Players.Count >= 2)
        {
            await Clients.Caller.SendAsync("Error", "Match is full");
            return;
        }

        player.IsHost = false;
        match.Players.Add(player);
        _connectionToMatch[Context.ConnectionId] = matchCode;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"match:{matchCode}");

        // Notify all players in match
        await Clients.Group($"match:{matchCode}").SendAsync("PlayerJoined", new
        {
            player.PlayerId,
            player.DisplayName,
            Players = match.Players.Select(p => new { p.PlayerId, p.DisplayName, p.IsHost })
        });

        _logger.LogInformation("Player {DisplayName} joined match {MatchCode}", player.DisplayName, matchCode);
    }

    /// <summary>
    /// Host starts the match
    /// </summary>
    public async Task StartMatch()
    {
        if (!_connectionToMatch.TryGetValue(Context.ConnectionId, out var matchCode))
        {
            await Clients.Caller.SendAsync("Error", "Not in a match");
            return;
        }

        if (!_matches.TryGetValue(matchCode, out var match))
        {
            await Clients.Caller.SendAsync("Error", "Match not found");
            return;
        }

        if (match.HostConnectionId != Context.ConnectionId)
        {
            await Clients.Caller.SendAsync("Error", "Only host can start the match");
            return;
        }

        if (match.Players.Count < 2)
        {
            await Clients.Caller.SendAsync("Error", "Need at least 2 players");
            return;
        }

        match.State = MatchState.Playing;
        match.CurrentPlayerIndex = 0;
        match.StartedAt = DateTime.UtcNow;

        await Clients.Group($"match:{matchCode}").SendAsync("MatchStarted", new
        {
            match.MatchCode,
            match.GameMode,
            match.BestOf,
            Players = match.Players.Select(p => new { p.PlayerId, p.DisplayName, p.IsHost }),
            CurrentPlayer = match.Players[0].DisplayName
        });

        _logger.LogInformation("Match started: {MatchCode}", matchCode);
    }

    /// <summary>
    /// Relay a dart throw to all players in the match
    /// </summary>
    public async Task RelayDart(int segment, int multiplier, int score, string zone)
    {
        if (!_connectionToMatch.TryGetValue(Context.ConnectionId, out var matchCode))
        {
            await Clients.Caller.SendAsync("Error", "Not in a match");
            return;
        }

        if (!_matches.TryGetValue(matchCode, out var match))
        {
            await Clients.Caller.SendAsync("Error", "Match not found");
            return;
        }

        if (!_players.TryGetValue(Context.ConnectionId, out var player))
        {
            return;
        }

        // Broadcast dart to all players in match
        await Clients.Group($"match:{matchCode}").SendAsync("DartRelayed", new
        {
            PlayerId = player.PlayerId,
            PlayerName = player.DisplayName,
            Segment = segment,
            Multiplier = multiplier,
            Score = score,
            Zone = zone,
            Timestamp = DateTime.UtcNow
        });

        _logger.LogDebug("Dart relayed in {MatchCode}: {Zone} {Segment} = {Score}", matchCode, zone, segment, score);
    }

    /// <summary>
    /// Relay score update to all players
    /// </summary>
    public async Task RelayScoreUpdate(string playerId, int newScore, int dartsThrown, int legsWon)
    {
        if (!_connectionToMatch.TryGetValue(Context.ConnectionId, out var matchCode))
        {
            return;
        }

        await Clients.Group($"match:{matchCode}").SendAsync("ScoreUpdated", new
        {
            PlayerId = playerId,
            Score = newScore,
            DartsThrown = dartsThrown,
            LegsWon = legsWon,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Relay turn end to advance to next player
    /// </summary>
    public async Task RelayTurnEnd(int turnScore, bool busted)
    {
        if (!_connectionToMatch.TryGetValue(Context.ConnectionId, out var matchCode))
        {
            return;
        }

        if (!_matches.TryGetValue(matchCode, out var match))
        {
            return;
        }

        if (!_players.TryGetValue(Context.ConnectionId, out var player))
        {
            return;
        }

        // Advance to next player
        match.CurrentPlayerIndex = (match.CurrentPlayerIndex + 1) % match.Players.Count;
        var nextPlayer = match.Players[match.CurrentPlayerIndex];

        await Clients.Group($"match:{matchCode}").SendAsync("TurnEnded", new
        {
            PlayerId = player.PlayerId,
            PlayerName = player.DisplayName,
            TurnScore = turnScore,
            Busted = busted,
            NextPlayerId = nextPlayer.PlayerId,
            NextPlayerName = nextPlayer.DisplayName
        });
    }

    /// <summary>
    /// Relay leg/match win
    /// </summary>
    public async Task RelayWin(string playerId, bool matchWon, int legsWon)
    {
        if (!_connectionToMatch.TryGetValue(Context.ConnectionId, out var matchCode))
        {
            return;
        }

        if (!_matches.TryGetValue(matchCode, out var match))
        {
            return;
        }

        var winner = match.Players.FirstOrDefault(p => p.PlayerId == playerId);

        if (matchWon)
        {
            match.State = MatchState.Finished;
            match.WinnerId = playerId;
            match.FinishedAt = DateTime.UtcNow;

            await Clients.Group($"match:{matchCode}").SendAsync("MatchWon", new
            {
                WinnerId = playerId,
                WinnerName = winner?.DisplayName,
                LegsWon = legsWon,
                match.MatchCode
            });

            _logger.LogInformation("Match {MatchCode} won by {Winner}", matchCode, winner?.DisplayName);
        }
        else
        {
            await Clients.Group($"match:{matchCode}").SendAsync("LegWon", new
            {
                WinnerId = playerId,
                WinnerName = winner?.DisplayName,
                LegsWon = legsWon
            });
        }
    }

    /// <summary>
    /// Send chat message to match
    /// </summary>
    public async Task SendChat(string message)
    {
        if (!_connectionToMatch.TryGetValue(Context.ConnectionId, out var matchCode))
        {
            return;
        }

        if (!_players.TryGetValue(Context.ConnectionId, out var player))
        {
            return;
        }

        await Clients.Group($"match:{matchCode}").SendAsync("ChatMessage", new
        {
            player.PlayerId,
            player.DisplayName,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Leave current match
    /// </summary>
    public async Task LeaveMatch()
    {
        if (!_connectionToMatch.TryRemove(Context.ConnectionId, out var matchCode))
        {
            return;
        }

        if (!_players.TryGetValue(Context.ConnectionId, out var player))
        {
            return;
        }

        if (_matches.TryGetValue(matchCode, out var match))
        {
            match.Players.Remove(player);

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"match:{matchCode}");

            await Clients.Group($"match:{matchCode}").SendAsync("PlayerLeft", new
            {
                player.PlayerId,
                player.DisplayName
            });

            // Clean up empty matches
            if (match.Players.Count == 0)
            {
                _matches.TryRemove(matchCode, out _);
            }
        }

        await Clients.Caller.SendAsync("LeftMatch", new { matchCode });
    }

    /// <summary>
    /// Get list of open matches (for lobby)
    /// </summary>
    public async Task GetOpenMatches()
    {
        var openMatches = _matches.Values
            .Where(m => m.State == MatchState.Waiting && m.Players.Count < 2)
            .Select(m => new
            {
                m.MatchCode,
                m.GameMode,
                m.BestOf,
                HostName = m.Players.FirstOrDefault(p => p.IsHost)?.DisplayName,
                PlayerCount = m.Players.Count,
                CreatedAt = m.CreatedAt
            })
            .ToList();

        await Clients.Caller.SendAsync("OpenMatches", openMatches);
    }

    /// <summary>
    /// Join matchmaking queue
    /// </summary>
    public async Task JoinMatchmakingQueue(string gameMode, string preference = "anyone", int? minRating = null, int? maxRating = null)
    {
        if (!_connectionToPlayerId.TryGetValue(Context.ConnectionId, out var playerId))
        {
            await Clients.Caller.SendAsync("Error", "Please register first");
            return;
        }

        var pref = preference.ToLower() switch
        {
            "similar" => Models.MatchmakingPreference.SimilarSkill,
            "chosen" => Models.MatchmakingPreference.ChosenSkill,
            "friends" => Models.MatchmakingPreference.FriendsOnly,
            _ => Models.MatchmakingPreference.Anyone
        };

        await _matchmaking.JoinQueue(playerId, Guid.Empty, gameMode, pref, minRating, maxRating);
    }

    /// <summary>
    /// Leave matchmaking queue
    /// </summary>
    public async Task LeaveMatchmakingQueue()
    {
        if (_connectionToPlayerId.TryGetValue(Context.ConnectionId, out var playerId))
        {
            await _matchmaking.LeaveQueue(playerId);
        }
    }

    /// <summary>
    /// Get friends with online status
    /// </summary>
    public async Task GetFriends()
    {
        if (!_connectionToPlayerId.TryGetValue(Context.ConnectionId, out var playerId))
        {
            await Clients.Caller.SendAsync("Error", "Please register first");
            return;
        }

        var friends = await _matchmaking.GetFriendsWithStatus(playerId);
        await Clients.Caller.SendAsync("FriendsList", friends);
    }

    /// <summary>
    /// Get online player count
    /// </summary>
    public async Task GetOnlineCount()
    {
        var count = _matchmaking.GetOnlineCount();
        await Clients.Caller.SendAsync("OnlineCount", count);
    }

    /// <summary>
    /// Heartbeat to keep session alive
    /// </summary>
    public void Heartbeat()
    {
        if (_connectionToPlayerId.TryGetValue(Context.ConnectionId, out var playerId))
        {
            _matchmaking.UpdateHeartbeat(playerId);
        }
    }

    private static string GenerateMatchCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No confusing chars (0/O, 1/I)
        var random = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}

#region Models

public class OnlineMatch
{
    public string MatchCode { get; set; } = "";
    public string GameMode { get; set; } = "Game501";
    public int BestOf { get; set; } = 5;
    public string HostConnectionId { get; set; } = "";
    public List<OnlinePlayer> Players { get; set; } = new();
    public MatchState State { get; set; } = MatchState.Waiting;
    public int CurrentPlayerIndex { get; set; }
    public string? WinnerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public class OnlinePlayer
{
    public string ConnectionId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsHost { get; set; }
    public DateTime JoinedAt { get; set; }
}

public enum MatchState
{
    Waiting,
    Playing,
    Finished
}

#endregion

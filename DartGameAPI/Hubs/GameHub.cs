using Microsoft.AspNetCore.SignalR;
using DartGameAPI.Models;
using System.Collections.Concurrent;

namespace DartGameAPI.Hubs;

/// <summary>
/// SignalR hub for real-time game updates and sensor communication
/// </summary>
public class GameHub : Hub
{
    private readonly ILogger<GameHub> _logger;
    
    // Track connected sensors by board ID
    private static readonly ConcurrentDictionary<string, string> _sensorConnections = new();

    public GameHub(ILogger<GameHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Remove sensor registration if this was a sensor
        var boardId = _sensorConnections.FirstOrDefault(x => x.Value == Context.ConnectionId).Key;
        if (boardId != null)
        {
            _sensorConnections.TryRemove(boardId, out _);
            _logger.LogInformation("Sensor disconnected for board {BoardId}", boardId);
            
            // Notify UI clients that sensor disconnected
            await Clients.Group($"board:{boardId}").SendAsync("SensorDisconnected", new { boardId });
        }
        
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Register a sensor for a board
    /// </summary>
    public async Task RegisterBoard(string boardId)
    {
        _sensorConnections[boardId] = Context.ConnectionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"sensor:{boardId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"board:{boardId}");
        
        _logger.LogInformation("Sensor registered for board {BoardId}: {ConnectionId}", boardId, Context.ConnectionId);
        await Clients.Caller.SendAsync("Registered", boardId);
        
        // Notify UI clients that sensor connected
        await Clients.Group($"board:{boardId}").SendAsync("SensorConnected", new { boardId });
    }

    /// <summary>
    /// Join a board's update channel (for UI clients)
    /// </summary>
    public async Task JoinBoard(string boardId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"board:{boardId}");
        _logger.LogInformation("Client {ConnectionId} joined board {BoardId}", Context.ConnectionId, boardId);
    }

    /// <summary>
    /// Leave a board's update channel
    /// </summary>
    public async Task LeaveBoard(string boardId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"board:{boardId}");
        _logger.LogInformation("Client {ConnectionId} left board {BoardId}", Context.ConnectionId, boardId);
    }
    
    /// <summary>
    /// Check if a sensor is connected for a board
    /// </summary>
    public static bool IsSensorConnected(string boardId)
    {
        return _sensorConnections.ContainsKey(boardId);
    }
    
    /// <summary>
    /// Get connection ID for a board's sensor
    /// </summary>
    public static string? GetSensorConnectionId(string boardId)
    {
        return _sensorConnections.TryGetValue(boardId, out var connId) ? connId : null;
    }
}

/// <summary>
/// Extension methods for broadcasting game events
/// </summary>
public static class GameHubExtensions
{
    /// <summary>
    /// Tell a sensor to start detecting (game started)
    /// </summary>
    public static async Task SendStartGame(this IHubContext<GameHub> hub, string boardId, string gameId)
    {
        var connId = GameHub.GetSensorConnectionId(boardId);
        if (connId != null)
        {
            await hub.Clients.Client(connId).SendAsync("StartGame", gameId);
        }
        else
        {
            // Fall back to group (in case sensor joined via group)
            await hub.Clients.Group($"sensor:{boardId}").SendAsync("StartGame", gameId);
        }
    }
    
    /// <summary>
    /// Tell a sensor to stop detecting (game ended)
    /// </summary>
    public static async Task SendStopGame(this IHubContext<GameHub> hub, string boardId)
    {
        var connId = GameHub.GetSensorConnectionId(boardId);
        if (connId != null)
        {
            await hub.Clients.Client(connId).SendAsync("StopGame", boardId);
        }
        else
        {
            await hub.Clients.Group($"sensor:{boardId}").SendAsync("StopGame", boardId);
        }
    }
    
    /// <summary>
    /// Tell a sensor to capture new baseline (darts removed)
    /// </summary>
    public static async Task SendRebase(this IHubContext<GameHub> hub, string boardId)
    {
        var connId = GameHub.GetSensorConnectionId(boardId);
        if (connId != null)
        {
            await hub.Clients.Client(connId).SendAsync("Rebase", boardId);
        }
        else
        {
            await hub.Clients.Group($"sensor:{boardId}").SendAsync("Rebase", boardId);
        }
    }

    /// <summary>
    /// Notify clients that a dart was thrown
    /// </summary>
    public static async Task SendDartThrown(this IHubContext<GameHub> hub, string boardId, DartThrow dart, Game game)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("DartThrown", new
        {
            dart,
            game = new
            {
                game.Id,
                game.Mode,
                game.State,
                game.WinnerId,
                WinnerName = game.Players.FirstOrDefault(p => p.Id == game.WinnerId)?.Name,
                game.CurrentPlayerIndex,
                CurrentPlayer = game.CurrentPlayer?.Name,
                Players = game.Players.Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Score,
                    p.DartsThrown,
                    p.LegsWon
                }).ToList(),
                CurrentTurn = game.CurrentTurn == null ? null : new
                {
                    game.CurrentTurn.TurnNumber,
                    game.CurrentTurn.TurnScore,
                    Busted = game.CurrentTurn.IsBusted,
                    ScoreBeforeBust = game.CurrentTurn.ScoreBeforeBust,
                    Darts = game.CurrentTurn.Darts.Select(d => new
                    {
                        d.Index,
                        d.Segment,
                        d.Multiplier,
                        d.Zone,
                        d.Score
                    }).ToList()
                }
            }
        });
    }

    /// <summary>
    /// Notify clients that the board was cleared
    /// </summary>
    public static async Task SendBoardCleared(this IHubContext<GameHub> hub, string boardId)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("BoardCleared", new { boardId });
    }

    /// <summary>
    /// Notify clients that a dart was removed (false detection)
    /// </summary>
    public static async Task SendDartRemoved(this IHubContext<GameHub> hub, string boardId, DartThrow removedDart, Game game)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("DartRemoved", new
        {
            removedDart = new
            {
                removedDart.Index,
                removedDart.Segment,
                removedDart.Multiplier,
                removedDart.Zone,
                removedDart.Score
            },
            game = new
            {
                game.Id,
                game.Mode,
                game.State,
                game.CurrentPlayerIndex,
                CurrentPlayer = game.CurrentPlayer?.Name,
                Players = game.Players.Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Score,
                    p.DartsThrown,
                    p.LegsWon
                }).ToList(),
                CurrentTurn = game.CurrentTurn == null ? null : new
                {
                    game.CurrentTurn.TurnNumber,
                    game.CurrentTurn.TurnScore,
                    Darts = game.CurrentTurn.Darts.Select(d => new
                    {
                        d.Index,
                        d.Segment,
                        d.Multiplier,
                        d.Zone,
                        d.Score
                    }).ToList()
                }
            }
        });
    }

    /// <summary>
    /// Notify clients that a game started
    /// </summary>
    public static async Task SendGameStarted(this IHubContext<GameHub> hub, string boardId, Game game)
    {
        // Notify UI clients
        await hub.Clients.Group($"board:{boardId}").SendAsync("GameStarted", new
        {
            game.Id,
            game.BoardId,
            game.Mode,
            game.State,
            Players = game.Players.Select(p => new { p.Id, p.Name, p.Score })
        });
        
        // Tell sensor to start detecting
        await hub.SendStartGame(boardId, game.Id);
    }

    /// <summary>
    /// Notify clients that a game ended
    /// </summary>

    /// <summary>
    /// Notify clients that a leg was won (game continues)
    /// </summary>
    public static async Task SendLegWon(this IHubContext<GameHub> hub, string boardId, string winnerName, int legsWon, int legsToWin, Game game)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("LegWon", new
        {
            winnerName,
            legsWon,
            legsToWin,
            currentLeg = game.CurrentLeg,
            game = new { game.Id, game.State, Players = game.Players.Select(p => new { p.Id, p.Name, p.Score, p.LegsWon, p.DartsThrown }) }
        });
    }
    public static async Task SendGameEnded(this IHubContext<GameHub> hub, string boardId, Game game)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("GameEnded", new
        {
            game.Id,
            game.WinnerId,
            WinnerName = game.Players.FirstOrDefault(p => p.Id == game.WinnerId)?.Name,
            Players = game.Players.Select(p => new { p.Id, p.Name, p.Score, p.DartsThrown, p.LegsWon })
        });
        
        // Tell sensor to stop detecting
        await hub.SendStopGame(boardId);
    }

    /// <summary>
    /// Notify clients that a turn ended
    /// </summary>
    public static async Task SendTurnEnded(this IHubContext<GameHub> hub, string boardId, Game game, Turn turn)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("TurnEnded", new
        {
            turn = new {
                turn.TurnNumber,
                turn.PlayerId,
                turn.TurnScore
            },
            game = new
            {
                game.Id,
                game.Mode,
                game.State,
                game.CurrentRound,
                game.CurrentPlayerIndex,
                CurrentPlayer = game.CurrentPlayer?.Name,
                Players = game.Players.Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Score,
                    p.DartsThrown,
                    p.LegsWon
                }).ToList(),
                CurrentTurn = game.CurrentTurn == null ? null : new
                {
                    game.CurrentTurn.TurnNumber,
                    game.CurrentTurn.TurnScore,
                    Darts = game.CurrentTurn.Darts.Select(d => new
                    {
                        d.Index,
                        d.Segment,
                        d.Multiplier,
                        d.Zone,
                        d.Score
                    }).ToList()
                }
            }
        });
        
        // Tell sensor to rebase (darts should be removed)
        await hub.SendRebase(boardId);
    }
}

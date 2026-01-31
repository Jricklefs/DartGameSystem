using Microsoft.AspNetCore.SignalR;
using DartGameAPI.Models;

namespace DartGameAPI.Hubs;

/// <summary>
/// SignalR hub for real-time game updates
/// </summary>
public class GameHub : Hub
{
    private readonly ILogger<GameHub> _logger;

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
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Join a board's update channel
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
}

/// <summary>
/// Extension methods for broadcasting game events
/// </summary>
public static class GameHubExtensions
{
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
                game.CurrentPlayerIndex,
                CurrentPlayer = game.CurrentPlayer?.Name,
                Players = game.Players.Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Score,
                    p.DartsThrown
                }),
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
                    })
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
    /// Notify clients that a game started
    /// </summary>
    public static async Task SendGameStarted(this IHubContext<GameHub> hub, string boardId, Game game)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("GameStarted", new
        {
            game.Id,
            game.BoardId,
            game.Mode,
            game.State,
            Players = game.Players.Select(p => new { p.Id, p.Name, p.Score })
        });
    }

    /// <summary>
    /// Notify clients that a game ended
    /// </summary>
    public static async Task SendGameEnded(this IHubContext<GameHub> hub, string boardId, Game game)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("GameEnded", new
        {
            game.Id,
            game.WinnerId,
            WinnerName = game.Players.FirstOrDefault(p => p.Id == game.WinnerId)?.Name,
            Players = game.Players.Select(p => new { p.Id, p.Name, p.Score, p.DartsThrown })
        });
    }

    /// <summary>
    /// Notify clients that a turn ended
    /// </summary>
    public static async Task SendTurnEnded(this IHubContext<GameHub> hub, string boardId, Game game, Turn turn)
    {
        await hub.Clients.Group($"board:{boardId}").SendAsync("TurnEnded", new
        {
            turn.TurnNumber,
            turn.PlayerId,
            turn.TurnScore,
            NextPlayer = game.CurrentPlayer?.Name,
            game.CurrentPlayerIndex
        });
    }
}

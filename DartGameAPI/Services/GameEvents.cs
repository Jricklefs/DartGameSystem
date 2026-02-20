using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// All game event types raised by the X01GameEngine.
/// Consumers (SignalR hub, logging, etc.) subscribe to these.
/// </summary>
public abstract class GameEvent
{
    public string GameId { get; set; } = string.Empty;
    public string BoardId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// === Match lifecycle ===
public class MatchStartedEvent : GameEvent
{
    public MatchConfig Config { get; set; } = null!;
    public List<string> PlayerNames { get; set; } = new();
}

public class LegStartedEvent : GameEvent
{
    public int LegNumber { get; set; }
    public string StartingPlayerId { get; set; } = string.Empty;
}

public class SetStartedEvent : GameEvent
{
    public int SetNumber { get; set; }
}

// === Turn lifecycle ===
public class TurnStartedEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public int TurnStartScore { get; set; }
}

public class DartThrownEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public DartThrow Dart { get; set; } = null!;
    public int ScoreAfter { get; set; }
    public DartResultType ResultType { get; set; }
}

public class TurnEndedEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public int TurnTotal { get; set; }
    public int DartsUsed { get; set; }
}

// === Bust ===
public class BustDetectedEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public PendingBust PendingBust { get; set; } = null!;
}

public class BustConfirmedEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public int RevertedToScore { get; set; }
}

public class BustOverriddenEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public DartThrow CorrectedDart { get; set; } = null!;
    public DartResultType ResultAfterCorrection { get; set; }
}

// === Win conditions ===
public class LegWonEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public int LegsWon { get; set; }
    public int LegsToWin { get; set; }
}

public class SetWonEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public int SetsWon { get; set; }
    public int SetsToWin { get; set; }
}

public class MatchWonEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
}

// === Sensor ===
public class SensorStartedEvent : GameEvent { }
public class SensorStoppedEvent : GameEvent { }
public class SensorRearmedEvent : GameEvent { }
public class SensorErrorEvent : GameEvent
{
    public string Error { get; set; } = string.Empty;
}

// === Correction ===
public class DartCorrectedEvent : GameEvent
{
    public string PlayerId { get; set; } = string.Empty;
    public int DartIndex { get; set; }
    public DartThrow OldDart { get; set; } = null!;
    public DartThrow NewDart { get; set; } = null!;
}

/// <summary>
/// Simple event dispatcher for game events.
/// Register handlers and raise events from the engine.
/// </summary>
public class GameEventDispatcher
{
    private readonly List<Func<GameEvent, Task>> _handlers = new();
    private readonly ILogger<GameEventDispatcher> _logger;

    public GameEventDispatcher(ILogger<GameEventDispatcher> logger)
    {
        _logger = logger;
    }

    public void Subscribe(Func<GameEvent, Task> handler)
    {
        _handlers.Add(handler);
    }

    public async Task RaiseAsync(GameEvent evt)
    {
        _logger.LogDebug("Game event: {EventType} for game {GameId}", evt.GetType().Name, evt.GameId);
        foreach (var handler in _handlers)
        {
            try
            {
                await handler(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling game event {EventType}", evt.GetType().Name);
            }
        }
    }
}

using DartGameAPI.Services;

namespace DartGameAPI.Models;

/// <summary>
/// Represents a physical dartboard with cameras
/// </summary>
public class Board
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> CameraIds { get; set; } = new();
    public bool IsCalibrated { get; set; }
    public DateTime? LastCalibration { get; set; }
    public string? CurrentGameId { get; set; }
}

/// <summary>
/// A dart throw with position and score
/// </summary>
public class DartThrow
{
    public int Index { get; set; }  // 0, 1, 2 for each throw in turn
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public string Zone { get; set; } = string.Empty;
    public int Score { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double Confidence { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A player's turn (up to 3 darts)
/// </summary>
public class Turn
{
    public int TurnNumber { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public List<DartThrow> Darts { get; set; } = new();
    public int TurnScore => Darts.Sum(d => d.Score);
    public bool IsComplete => Darts.Count >= 3;
    
    /// <summary>
    /// True if this turn resulted in a bust
    /// </summary>
    public bool IsBusted { get; set; } = false;
    
    /// <summary>
    /// Score before bust (so we can revert)
    /// </summary>
    public int ScoreBeforeBust { get; set; }

    /// <summary>
    /// True after UI confirms bust
    /// </summary>
    public bool BustConfirmed { get; set; } = false;

    // === New X01 engine fields ===

    /// <summary>
    /// Player's score at the START of this turn (before any darts).
    /// Used for bust revert and correction recompute.
    /// </summary>
    public int TurnStartScore { get; set; }

    /// <summary>
    /// Total points scored this turn (computed from darts)
    /// </summary>
    public int TurnTotalPoints => Darts.Sum(d => d.Score);

    /// <summary>
    /// Whether a bust is pending confirmation
    /// </summary>
    public bool BustPending { get; set; } = false;

    /// <summary>
    /// Whether this turn is currently active
    /// </summary>
    public bool IsTurnActive { get; set; } = false;
}

/// <summary>
/// A player in a game
/// </summary>
public class Player
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public int DartsThrown { get; set; }
    public int LegsWon { get; set; } = 0;
    public List<Turn> Turns { get; set; } = new();

    // === New X01 engine fields ===

    /// <summary>
    /// Whether the player has "doubled in" (for Double-In games).
    /// Always true when Double-In is not required.
    /// </summary>
    public bool IsIn { get; set; } = true;

    /// <summary>
    /// Sets won (when sets are enabled in match config)
    /// </summary>
    public int SetsWon { get; set; } = 0;
}

/// <summary>
/// Game modes. X01 is the generic mode; legacy Game501/Game301/Debug20 map to X01 + StartingScore.
/// </summary>
public enum GameMode
{
    Practice,   // No rules, just scoring
    Game501,    // Start at 501 (legacy, maps to X01)
    Game301,    // Start at 301 (legacy, maps to X01)
    Debug20,    // Start at 20 (legacy, maps to X01)
    Cricket,    // Hit 15-20 and bulls
    X01         // Generic X01 — uses MatchConfig for StartingScore and all options
}

/// <summary>
/// Game state
/// </summary>
public enum GameState
{
    WaitingForPlayers,
    InProgress,
    Finished
}

/// <summary>
/// A dart game session
/// </summary>
public class Game
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BoardId { get; set; } = string.Empty;
    public GameMode Mode { get; set; } = GameMode.Practice;
    public GameState State { get; set; } = GameState.WaitingForPlayers;
    public List<Player> Players { get; set; } = new();
    public int CurrentPlayerIndex { get; set; } = 0;
    public Turn? CurrentTurn { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public string? WinnerId { get; set; }
    
    // Legs (best of N)
    public int LegsToWin { get; set; } = 3;
    
    // Checkout rules
    public bool RequireDoubleOut { get; set; } = false;
    public int CurrentLeg { get; set; } = 1;
    public string? LegWinnerId { get; set; }
    
    // Round tracking
    public int CurrentRound { get; set; } = 1;

    /// <summary>Max darts per turn.</summary>
    public int DartsPerTurn { get; set; } = 3;

    /// <summary>Centralized game rules</summary>
    public GameRules Rules { get; set; } = new();
    
    // Known dart positions on board
    public List<KnownDart> KnownDarts { get; set; } = new();
    
    public Player? CurrentPlayer => Players.Count > CurrentPlayerIndex 
        ? Players[CurrentPlayerIndex] 
        : null;
        
    public int TotalLegs => (LegsToWin * 2) - 1;

    // === New X01 engine fields ===

    /// <summary>
    /// Full match configuration for X01 games.
    /// Null for legacy/Practice/Cricket games.
    /// </summary>
    public MatchConfig? MatchConfig { get; set; }

    /// <summary>
    /// Current state of the X01 engine state machine
    /// </summary>
    public EngineState EngineState { get; set; } = EngineState.MatchNotStarted;

    /// <summary>
    /// Pending busts awaiting confirmation or correction
    /// </summary>
    public List<PendingBust> PendingBusts { get; set; } = new();

    /// <summary>
    /// Whether this game is using the new X01 engine
    /// </summary>
    public bool IsX01Engine => Mode == GameMode.X01 ||
                               Mode == GameMode.Game501 ||
                               Mode == GameMode.Game301 ||
                               Mode == GameMode.Debug20;
}

/// <summary>
/// A dart we know is on the board
/// </summary>
public class KnownDart
{
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }
    public int Score { get; set; }
    public DateTime DetectedAt { get; set; }
}

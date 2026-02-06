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
    /// True if this turn resulted in a bust (score went negative, to 1, or 0 without double)
    /// When true, UI should show "BUSTED" and allow correction before proceeding
    /// </summary>
    public bool IsBusted { get; set; } = false;
    
    /// <summary>
    /// Score before bust (so we can revert if bust is corrected)
    /// </summary>
    public int ScoreBeforeBust { get; set; }
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
}

/// <summary>
/// Game modes
/// </summary>
public enum GameMode
{
    Practice,   // No rules, just scoring
    Game501,    // Start at 501, get to exactly 0 with double out
    Game301,    // Start at 301
    Cricket     // Hit 15-20 and bulls
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
    public int LegsToWin { get; set; } = 3;  // Best of 5 = first to 3
    public int CurrentLeg { get; set; } = 1;
    public string? LegWinnerId { get; set; }  // Who won the current/last leg
    
    // Round tracking (one round = each player throws once)
    public int CurrentRound { get; set; } = 1;
    
    // Known dart positions on board (to detect new vs existing)
    public List<KnownDart> KnownDarts { get; set; } = new();
    
    public Player? CurrentPlayer => Players.Count > CurrentPlayerIndex 
        ? Players[CurrentPlayerIndex] 
        : null;
        
    public int TotalLegs => (LegsToWin * 2) - 1;  // Best of 5 = 5 total possible
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

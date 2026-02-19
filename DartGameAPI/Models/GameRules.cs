namespace DartGameAPI.Models;

/// <summary>
/// Game rules configuration. Built from GameMode at game creation.
/// Centralizes all game-mode-specific rules so game logic can read from one place.
/// Extensible for future game modes (e.g., Around the World, Killer, etc.)
/// </summary>
public class GameRules
{
    /// <summary>Max darts per turn (typically 3)</summary>
    public int DartsPerTurn { get; set; } = 3;
    
    /// <summary>Starting score (501, 301, 0 for count-up modes)</summary>
    public int StartingScore { get; set; } = 0;
    
    /// <summary>Count down (X01) or count up (Practice/Cricket)</summary>
    public ScoringDirection Direction { get; set; } = ScoringDirection.CountUp;
    
    /// <summary>Must finish on a double</summary>
    public bool RequireDoubleOut { get; set; } = false;
    
    /// <summary>Must start scoring on a double</summary>
    public bool RequireDoubleIn { get; set; } = false;
    
    /// <summary>Which segments are in play (null = all). For Cricket: 15-20 + Bull</summary>
    public List<int>? ActiveSegments { get; set; } = null;
    
    /// <summary>Max rounds before game ends (0 = unlimited)</summary>
    public int MaxRounds { get; set; } = 0;
    
    /// <summary>Human-readable name for this ruleset</summary>
    public string DisplayName { get; set; } = "Practice";

    /// <summary>
    /// Build rules from a game mode. Central place for all mode-specific config.
    /// When you add a new GameMode enum value, add a case here and all the rules
    /// for that mode live in one place instead of scattered across services.
    /// </summary>
    public static GameRules FromMode(GameMode mode, bool? requireDoubleOut = null)
    {
        var rules = mode switch
        {
            GameMode.Game501 => new GameRules
            {
                DartsPerTurn = 3,
                StartingScore = 501,
                Direction = ScoringDirection.CountDown,
                RequireDoubleOut = requireDoubleOut ?? false,
                DisplayName = "501"
            },
            GameMode.Game301 => new GameRules
            {
                DartsPerTurn = 3,
                StartingScore = 301,
                Direction = ScoringDirection.CountDown,
                RequireDoubleOut = requireDoubleOut ?? false,
                DisplayName = "301"
            },
            GameMode.Debug20 => new GameRules
            {
                DartsPerTurn = 3,
                StartingScore = 20,
                Direction = ScoringDirection.CountDown,
                RequireDoubleOut = false,
                DisplayName = "Debug 20"
            },
            GameMode.Cricket => new GameRules
            {
                DartsPerTurn = 3,
                StartingScore = 0,
                Direction = ScoringDirection.CountUp,
                ActiveSegments = new List<int> { 15, 16, 17, 18, 19, 20, 25 },
                DisplayName = "Cricket"
            },
            _ => new GameRules
            {
                DartsPerTurn = 3,
                StartingScore = 0,
                Direction = ScoringDirection.CountUp,
                DisplayName = "Practice"
            }
        };

        // Override double-out if explicitly set by the caller
        if (requireDoubleOut.HasValue)
            rules.RequireDoubleOut = requireDoubleOut.Value;

        return rules;
    }
}

/// <summary>
/// Scoring direction — X01 counts down to zero, Practice/Cricket count up.
/// Used by GameRules.Direction to drive scoring logic.
/// </summary>
public enum ScoringDirection
{
    CountUp,    // Practice: score accumulates from 0
    CountDown   // X01: score decrements from StartingScore to 0
}

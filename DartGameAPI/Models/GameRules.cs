namespace DartGameAPI.Models;

/// <summary>
/// Game rules configuration. Built from GameMode at game creation.
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

    /// <summary>Must finish on a double or triple (Master Out)</summary>
    public bool MasterOut { get; set; } = false;
    
    /// <summary>Which segments are in play (null = all). For Cricket: 15-20 + Bull</summary>
    public List<int>? ActiveSegments { get; set; } = null;
    
    /// <summary>Max rounds before game ends (0 = unlimited)</summary>
    public int MaxRounds { get; set; } = 0;
    
    /// <summary>Human-readable name for this ruleset</summary>
    public string DisplayName { get; set; } = "Practice";

    /// <summary>
    /// Build rules from a game mode.
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
            GameMode.X01 => new GameRules
            {
                DartsPerTurn = 3,
                StartingScore = 501, // Default, overridden by MatchConfig
                Direction = ScoringDirection.CountDown,
                RequireDoubleOut = requireDoubleOut ?? false,
                DisplayName = "X01"
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

        if (requireDoubleOut.HasValue)
            rules.RequireDoubleOut = requireDoubleOut.Value;

        return rules;
    }
}

/// <summary>
/// Scoring direction
/// </summary>
public enum ScoringDirection
{
    CountUp,
    CountDown
}

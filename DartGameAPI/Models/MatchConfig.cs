namespace DartGameAPI.Models;

/// <summary>
/// Configuration for an X01 match. Covers all variants (301-1001, Debug20)
/// with Double-In, Double-Out, Master-Out, Sets/Legs, and starting player rules.
/// </summary>
public class MatchConfig
{
    /// <summary>Starting score: 301, 401, 501, 601, 701, 801, 901, 1001, or 20 (debug)</summary>
    public int StartingScore { get; set; } = 501;

    /// <summary>Must hit a double to start scoring</summary>
    public bool DoubleIn { get; set; } = false;

    /// <summary>Must finish on a double</summary>
    public bool DoubleOut { get; set; } = false;

    /// <summary>Must finish on a double or triple (supercedes DoubleOut when true)</summary>
    public bool MasterOut { get; set; } = false;

    /// <summary>Legs required to win the match (or a set if SetsEnabled)</summary>
    public int LegsToWin { get; set; } = 3;

    /// <summary>Enable sets (best-of-sets, each set is best-of-legs)</summary>
    public bool SetsEnabled { get; set; } = false;

    /// <summary>Sets required to win the match</summary>
    public int SetsToWin { get; set; } = 3;

    /// <summary>Legs required to win a set (only used when SetsEnabled)</summary>
    public int LegsPerSet { get; set; } = 3;

    /// <summary>How to determine starting player for each leg</summary>
    public StartingPlayerRule StartingPlayerRule { get; set; } = StartingPlayerRule.Alternate;

    /// <summary>Darts per turn (default 3)</summary>
    public int DartsPerTurn { get; set; } = 3;

    /// <summary>
    /// Create a MatchConfig from legacy GameMode for backward compatibility
    /// </summary>
    public static MatchConfig FromLegacyMode(GameMode mode, bool requireDoubleOut = false, int bestOf = 5)
    {
        int legsToWin = (bestOf / 2) + 1;
        return mode switch
        {
            GameMode.Game501 => new MatchConfig { StartingScore = 501, DoubleOut = requireDoubleOut, LegsToWin = legsToWin },
            GameMode.Game301 => new MatchConfig { StartingScore = 301, DoubleOut = requireDoubleOut, LegsToWin = legsToWin },
            GameMode.Debug20 => new MatchConfig { StartingScore = 20, DoubleOut = false, LegsToWin = legsToWin },
            GameMode.X01 => new MatchConfig { StartingScore = 501, DoubleOut = requireDoubleOut, LegsToWin = legsToWin },
            _ => new MatchConfig { StartingScore = 501, LegsToWin = legsToWin }
        };
    }
}

/// <summary>
/// Rule for determining which player starts each leg
/// </summary>
public enum StartingPlayerRule
{
    /// <summary>Alternate starting player each leg</summary>
    Alternate,
    /// <summary>Winner of previous leg starts next</summary>
    WinnerStarts,
    /// <summary>Rotate through players in fixed order</summary>
    FixedRotation
}

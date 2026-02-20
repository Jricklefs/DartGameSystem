namespace DartGameAPI.Models;

/// <summary>
/// A request to correct a previously thrown dart
/// </summary>
public class CorrectionRequest
{
    public string TurnId { get; set; } = string.Empty;
    public int DartIndex { get; set; }
    public DartThrow CorrectedDart { get; set; } = null!;
    public CorrectionMode CorrectionMode { get; set; } = CorrectionMode.RecomputeFromTurnStart;
}

/// <summary>
/// How to apply a dart correction
/// </summary>
public enum CorrectionMode
{
    /// <summary>Recompute all darts from turn start score (default, safest)</summary>
    RecomputeFromTurnStart,
    /// <summary>Manually override the score (for edge cases)</summary>
    ManualScoreOverride
}

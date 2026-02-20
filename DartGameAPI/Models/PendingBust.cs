namespace DartGameAPI.Models;

/// <summary>
/// Represents a bust that needs confirmation or correction.
/// Created when ProcessDart detects a bust condition.
/// </summary>
public class PendingBust
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PlayerId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public int TurnStartScore { get; set; }
    
    /// <summary>Which dart (0-based index) caused the bust</summary>
    public int DartIndex { get; set; }
    
    /// <summary>The dart throw that caused the bust</summary>
    public DartThrow OriginalDart { get; set; } = null!;
    
    /// <summary>Reason for bust: "negative", "score_is_1", "invalid_checkout"</summary>
    public string Reason { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

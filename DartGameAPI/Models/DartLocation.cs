using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DartGameAPI.Models;

/// <summary>
/// Stores dart tip locations for heatmap analysis.
/// Each row represents a single detected dart throw with precise coordinates.
/// </summary>
[Table("DartLocations")]
public class DartLocation
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Game ID this dart belongs to
    /// </summary>
    public string? GameId { get; set; }
    
    /// <summary>
    /// Player who threw this dart (if known)
    /// </summary>
    public string? PlayerId { get; set; }
    
    /// <summary>
    /// Turn number within the game
    /// </summary>
    public int TurnNumber { get; set; }
    
    /// <summary>
    /// Dart index within the turn (0, 1, 2)
    /// </summary>
    public int DartIndex { get; set; }
    
    /// <summary>
    /// X coordinate in mm from board center
    /// </summary>
    public double XMm { get; set; }
    
    /// <summary>
    /// Y coordinate in mm from board center
    /// </summary>
    public double YMm { get; set; }
    
    /// <summary>
    /// Segment number (1-20, 25 for bull)
    /// </summary>
    public int Segment { get; set; }
    
    /// <summary>
    /// Multiplier (1=single, 2=double, 3=triple)
    /// </summary>
    public int Multiplier { get; set; }
    
    /// <summary>
    /// Score for this dart (segment * multiplier)
    /// </summary>
    public int Score { get; set; }
    
    /// <summary>
    /// Detection confidence (0-1)
    /// </summary>
    public double Confidence { get; set; }
    
    /// <summary>
    /// Camera that detected this dart
    /// </summary>
    public string? CameraId { get; set; }
    
    /// <summary>
    /// When this dart was detected
    /// </summary>
    public DateTime DetectedAt { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace DartGameAPI.Models;

public class Calibration
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string CameraId { get; set; } = string.Empty;
    
    [MaxLength(500)]
    public string? CalibrationImagePath { get; set; }
    
    [MaxLength(500)]
    public string? OverlayImagePath { get; set; }
    
    public double Quality { get; set; }
    
    public double? TwentyAngle { get; set; }
    
    [MaxLength(100)]
    public string? CalibrationModel { get; set; }  // Model used for calibration (e.g., "default", "11m")
    
    public string? CalibrationData { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CalibrationDto
{
    public string CameraId { get; set; } = string.Empty;
    public string? CalibrationImagePath { get; set; }
    public string? OverlayImagePath { get; set; }
    public string? CalibrationImage { get; set; }  // Base64 for upload
    public string? OverlayImage { get; set; }      // Base64 for upload
    public double Quality { get; set; }
    public double? TwentyAngle { get; set; }
    public string? CalibrationModel { get; set; }  // Model used for calibration
    public string? CalibrationData { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class Mark20Request
{
    public string CameraId { get; set; } = string.Empty;
    public double X { get; set; }  // Click X coordinate (0-1 normalized)
    public double Y { get; set; }  // Click Y coordinate (0-1 normalized)
}

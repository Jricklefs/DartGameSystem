using System.ComponentModel.DataAnnotations;

namespace DartGameAPI.Models;

public class Calibration
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string CameraId { get; set; } = string.Empty;
    
    public byte[] CalibrationImage { get; set; } = Array.Empty<byte>();
    
    public byte[]? OverlayImage { get; set; }
    
    public double Quality { get; set; }
    
    public double? TwentyAngle { get; set; }
    
    public string? CalibrationData { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CalibrationDto
{
    public string CameraId { get; set; } = string.Empty;
    public string? CalibrationImage { get; set; }  // Base64
    public string? OverlayImage { get; set; }       // Base64
    public double Quality { get; set; }
    public double? TwentyAngle { get; set; }
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

namespace DartGameAPI.Models;

/// <summary>
/// Request/Response models for DartDetect API
/// </summary>

public class CameraImage
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;  // Base64
}

public class CalibrateRequest
{
    public List<CameraImage> Cameras { get; set; } = new();
}

public class CameraCalibrationResult
{
    public string CameraId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public double? Quality { get; set; }
    public string? OverlayImage { get; set; }
    public int? SegmentAtTop { get; set; }
    public string? Error { get; set; }
}

public class CalibrateResponse
{
    public List<CameraCalibrationResult> Results { get; set; } = new();
}

public class DetectRequest
{
    public List<CameraImage> Cameras { get; set; } = new();
    public double? RotationOffsetDegrees { get; set; }
}

public class DetectedTip
{
    public double XMm { get; set; }
    public double YMm { get; set; }
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public string Zone { get; set; } = string.Empty;
    public int Score { get; set; }
    public double Confidence { get; set; }
    public List<string> CamerasSeen { get; set; } = new();
}

public class CameraDetectionResult
{
    public string CameraId { get; set; } = string.Empty;
    public int TipsDetected { get; set; }
    public string? Error { get; set; }
}

public class DetectResponse
{
    public string RequestId { get; set; } = string.Empty;
    public int ProcessingMs { get; set; }
    public List<DetectedTip> Tips { get; set; } = new();
    public List<CameraDetectionResult> CameraResults { get; set; } = new();
}

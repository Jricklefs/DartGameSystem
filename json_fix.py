import sys
sys.stdout.reconfigure(encoding='utf-8')

path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Services\DartDetectNative.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Add using if needed
if 'using System.Text.Json.Serialization;' not in content:
    content = content.replace('using System.Text.Json;', 'using System.Text.Json;\nusing System.Text.Json.Serialization;')

# Replace DetectionResult class and everything after it
old_start = '/// <summary>\n/// Result from native dart detection.\n/// </summary>'
idx = content.find(old_start)
if idx < 0:
    old_start = '/// <summary>\r\n/// Result from native dart detection.\r\n/// </summary>'
    idx = content.find(old_start)

if idx >= 0:
    content = content[:idx] + '''/// <summary>
/// Result from native dart detection.
/// </summary>
public class DetectionResult
{
    [JsonPropertyName("segment")]
    public int Segment { get; set; }
    [JsonPropertyName("multiplier")]
    public int Multiplier { get; set; }
    [JsonPropertyName("score")]
    public int Score { get; set; }
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
    [JsonPropertyName("total_error")]
    public double TotalError { get; set; }
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    [JsonPropertyName("per_camera")]
    public Dictionary<string, CameraVote>? PerCamera { get; set; }
    [JsonPropertyName("coords_x")]
    public double? CoordsX { get; set; }
    [JsonPropertyName("coords_y")]
    public double? CoordsY { get; set; }
    [JsonPropertyName("debug_lines")]
    public Dictionary<string, CamDebugInfo>? DebugLines { get; set; }
}

public class CamDebugInfo
{
    [JsonPropertyName("ls_x")]
    public double LsX { get; set; }
    [JsonPropertyName("ls_y")]
    public double LsY { get; set; }
    [JsonPropertyName("le_x")]
    public double LeX { get; set; }
    [JsonPropertyName("le_y")]
    public double LeY { get; set; }
    [JsonPropertyName("tip_nx")]
    public double TipNx { get; set; }
    [JsonPropertyName("tip_ny")]
    public double TipNy { get; set; }
    [JsonPropertyName("tip_px")]
    public double TipPx { get; set; }
    [JsonPropertyName("tip_py")]
    public double TipPy { get; set; }
    [JsonPropertyName("mask_q")]
    public double MaskQ { get; set; }
    [JsonPropertyName("tip_reliable")]
    public bool TipReliable { get; set; }
    [JsonPropertyName("tip_dist")]
    public double TipDist { get; set; }
}

/// <summary>
/// Per-camera vote result.
/// </summary>
public class CameraVote
{
    [JsonPropertyName("segment")]
    public int Segment { get; set; }
    [JsonPropertyName("multiplier")]
    public int Multiplier { get; set; }
    [JsonPropertyName("score")]
    public int Score { get; set; }
    [JsonPropertyName("zone")]
    public string Zone { get; set; } = "";
}
'''
else:
    print("ERROR: Could not find DetectionResult class marker")
    sys.exit(1)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("DartDetectNative.cs rewritten with JsonPropertyName attributes")

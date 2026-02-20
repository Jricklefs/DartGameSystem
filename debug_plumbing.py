import sys
sys.stdout.reconfigure(encoding='utf-8')

# 1. Add debug fields to DetectionResult
path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Services\DartDetectNative.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

old = '''public class DetectionResult
{
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public string Method { get; set; } = "";
    public double Confidence { get; set; }
    public double TotalError { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, CameraVote>? PerCamera { get; set; }
}'''

new = '''public class DetectionResult
{
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public string Method { get; set; } = "";
    public double Confidence { get; set; }
    public double TotalError { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, CameraVote>? PerCamera { get; set; }
    
    // Debug/diagnostics from line intersection triangulation
    public double? CoordsX { get; set; }
    public double? CoordsY { get; set; }
    public Dictionary<string, CamDebugInfo>? DebugLines { get; set; }
}

public class CamDebugInfo
{
    public double LsX { get; set; }
    public double LsY { get; set; }
    public double LeX { get; set; }
    public double LeY { get; set; }
    public double TipNx { get; set; }
    public double TipNy { get; set; }
    public double TipPx { get; set; }
    public double TipPy { get; set; }
    public double MaskQ { get; set; }
    public bool TipReliable { get; set; }
    public double TipDist { get; set; }
}'''

content = content.replace(old, new)
with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("DartDetectNative.cs patched - added debug fields")

# 2. Plumb debug data through benchmark endpoint
path2 = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Controllers\GamesController.cs'
with open(path2, 'r', encoding='utf-8') as f:
    content = f.read()

# Store the raw native result for debug output
old2 = '''        var boardId = request.BoardId ?? "default";
        var detectResult = await _dartDetect.DetectAsync(images, boardId, 1, beforeImages);

        var totalMs = sw.ElapsedMilliseconds;

        if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            return Ok(new { 
                message = "No darts detected", 
                darts = new List<object>(),
                processingMs = totalMs,
                requestId
            });
        }

        var tip = detectResult.Tips.OrderByDescending(t => t.Confidence).First();

        return Ok(new {
            message = "Dart detected",
            darts = new[] { new { tip.Zone, tip.Score, tip.Segment, tip.Multiplier, tip.Confidence } },
            processingMs = totalMs,
            requestId,
            isNative = _dartDetect is NativeDartDetectService
        });'''

new2 = '''        var boardId = request.BoardId ?? "default";
        
        // For native detection, also get raw result with debug data
        DetectionResult? nativeResult = null;
        if (_dartDetect is NativeDartDetectService)
        {
            nativeResult = DartDetectNative.Detect(1, boardId,
                images.Select(i => Convert.FromBase64String(i.Image)).ToArray(),
                beforeImages?.Select(i => Convert.FromBase64String(i.Image)).ToArray() 
                    ?? images.Select(_ => Array.Empty<byte>()).ToArray());
        }
        
        var detectResult = await _dartDetect.DetectAsync(images, boardId, 1, beforeImages);
        var totalMs = sw.ElapsedMilliseconds;

        if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            return Ok(new { 
                message = "No darts detected", 
                darts = new List<object>(),
                processingMs = totalMs,
                requestId
            });
        }

        var tip = detectResult.Tips.OrderByDescending(t => t.Confidence).First();

        return Ok(new {
            message = "Dart detected",
            darts = new[] { new { tip.Zone, tip.Score, tip.Segment, tip.Multiplier, tip.Confidence } },
            processingMs = totalMs,
            requestId,
            isNative = _dartDetect is NativeDartDetectService,
            method = nativeResult?.Method,
            coordsX = nativeResult?.CoordsX,
            coordsY = nativeResult?.CoordsY,
            debugLines = nativeResult?.DebugLines
        });'''

content = content.replace(old2, new2)
with open(path2, 'w', encoding='utf-8') as f:
    f.write(content)
print("GamesController.cs patched - plumbed debug data through benchmark endpoint")

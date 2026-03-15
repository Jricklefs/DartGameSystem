using Microsoft.AspNetCore.Mvc;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public SettingsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("detection-mode")]
    public IActionResult GetDetectionMode()
    {
        var mode = _configuration.GetValue<string>("DetectionMode") ?? "hough";
        return Ok(new { mode });
    }

    [HttpPost("detection-mode")]
    public IActionResult SetDetectionMode([FromQuery] string mode)
    {
        if (mode != "hough" && mode != "pca")
            return BadRequest(new { error = "mode must be 'hough' or 'pca'" });
        
        _configuration["DetectionMode"] = mode;
        return Ok(new { mode, message = $"Detection mode set to {mode}" });
    }

    [HttpGet("calibration-source")]
    public IActionResult GetCalibrationSource()
    {
        var source = _configuration.GetValue<string>("CalibrationSource") ?? "yolo";
        return Ok(new { source });
    }

    [HttpPost("calibration-source")]
    public IActionResult SetCalibrationSource([FromQuery] string source)
    {
        if (source != "yolo" && source != "opencv")
            return BadRequest(new { error = "source must be 'yolo' or 'opencv'" });
        
        _configuration["CalibrationSource"] = source;
        return Ok(new { source, message = $"Calibration source set to {source}. Restart scoring to apply." });
    }
}

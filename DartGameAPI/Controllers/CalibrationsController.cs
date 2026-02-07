using DartGameAPI.Data;
using DartGameAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CalibrationsController : ControllerBase
{
    private readonly DartsMobDbContext _db;
    private readonly ILogger<CalibrationsController> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _calibrationDir;

    public CalibrationsController(
        DartsMobDbContext db,
        ILogger<CalibrationsController> logger,
        IWebHostEnvironment env,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _logger = logger;
        _env = env;
        _httpClientFactory = httpClientFactory;
        _calibrationDir = Path.Combine(_env.WebRootPath, "images", "calibrations");
        
        // Ensure directory exists
        if (!Directory.Exists(_calibrationDir))
            Directory.CreateDirectory(_calibrationDir);
    }

    /// <summary>
    /// Get all stored calibrations
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<CalibrationDto>>> GetAll()
    {
        var calibrations = await _db.Calibrations.ToListAsync();
        return calibrations.Select(ToDto).ToList();
    }

    /// <summary>
    /// Get calibration for a specific camera
    /// </summary>
    [HttpGet("{cameraId}")]
    public async Task<ActionResult<CalibrationDto>> Get(string cameraId)
    {
        var cal = await _db.Calibrations.FirstOrDefaultAsync(c => c.CameraId == cameraId);
        if (cal == null)
            return NotFound(new { error = $"No calibration found for {cameraId}" });
        
        return ToDto(cal);
    }

    /// <summary>
    /// Get the overlay image file directly
    /// </summary>
    [HttpGet("{cameraId}/overlay")]
    public async Task<IActionResult> GetOverlay(string cameraId)
    {
        var cal = await _db.Calibrations
            .Where(c => c.CameraId == cameraId)
            .Select(c => new { c.OverlayImagePath })
            .FirstOrDefaultAsync();
        
        if (string.IsNullOrEmpty(cal?.OverlayImagePath))
            return NotFound();
        
        var fullPath = Path.Combine(_env.WebRootPath, cal.OverlayImagePath.TrimStart('/'));
        if (!System.IO.File.Exists(fullPath))
            return NotFound();
        
        return PhysicalFile(fullPath, "image/png");
    }

    /// <summary>
    /// Save or update calibration for a camera
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CalibrationDto>> Save([FromBody] CalibrationDto dto)
    {
        if (string.IsNullOrEmpty(dto.CameraId))
            return BadRequest(new { error = "CameraId is required" });

        var existing = await _db.Calibrations.FirstOrDefaultAsync(c => c.CameraId == dto.CameraId);
        
        if (existing == null)
        {
            existing = new CalibrationEntity
            {
                CameraId = dto.CameraId,
                CreatedAt = DateTime.UtcNow
            };
            _db.Calibrations.Add(existing);
        }

        // Save calibration image to file
        if (!string.IsNullOrEmpty(dto.CalibrationImage))
        {
            var filename = $"{dto.CameraId}_calibration_{DateTime.UtcNow:yyyyMMddHHmmss}.jpg";
            var relativePath = $"/images/calibrations/{filename}";
            var fullPath = Path.Combine(_calibrationDir, filename);
            
            await System.IO.File.WriteAllBytesAsync(fullPath, Convert.FromBase64String(dto.CalibrationImage));
            existing.CalibrationImagePath = relativePath;
        }
        
        // Save overlay image to file
        if (!string.IsNullOrEmpty(dto.OverlayImage))
        {
            var filename = $"{dto.CameraId}_overlay_{DateTime.UtcNow:yyyyMMddHHmmss}.png";
            var relativePath = $"/images/calibrations/{filename}";
            var fullPath = Path.Combine(_calibrationDir, filename);
            
            await System.IO.File.WriteAllBytesAsync(fullPath, Convert.FromBase64String(dto.OverlayImage));
            existing.OverlayImagePath = relativePath;
        }
        
        existing.Quality = dto.Quality;
        existing.TwentyAngle = dto.TwentyAngle;
        existing.CalibrationData = dto.CalibrationData;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        
        _logger.LogInformation("Saved calibration for {CameraId}, quality: {Quality}, path: {Path}", 
            dto.CameraId, dto.Quality, existing.OverlayImagePath);

        return ToDto(existing);
    }

    /// <summary>
    /// Update the 20-angle for a camera (mark 20 feature)
    /// </summary>
    [HttpPost("{cameraId}/mark20")]
    public async Task<ActionResult<CalibrationDto>> Mark20(string cameraId, [FromBody] Mark20Request request)
    {
        var cal = await _db.Calibrations.FirstOrDefaultAsync(c => c.CameraId == cameraId);
        if (cal == null)
            return NotFound(new { error = $"No calibration found for {cameraId}" });

        // Call DartDetectionAI to update segment_20_index in its calibration store
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            var dartDetectUrl = $"http://192.168.0.158:8000/api/calibrations/{cameraId}/mark20?x={request.X}&y={request.Y}";
            var response = await httpClient.PostAsync(dartDetectUrl, null);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<Mark20Response>();
                if (result != null)
                {
                    cal.TwentyAngle = result.TwentyAngle;
                    _logger.LogInformation("DartDetect updated segment_20_index to {Index} for {CameraId}", 
                        result.Segment20Index, cameraId);
                }
            }
            else
            {
                _logger.LogWarning("DartDetect mark20 failed: {Status}", response.StatusCode);
                // Fall back to simple angle calculation
                double centerX = 0.5, centerY = 0.5;
                double dx = request.X - centerX;
                double dy = request.Y - centerY;
                double angle = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
                if (angle < 0) angle += 360;
                cal.TwentyAngle = angle;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to call DartDetect mark20, using fallback calculation");
            // Fall back to simple angle calculation
            double centerX = 0.5, centerY = 0.5;
            double dx = request.X - centerX;
            double dy = request.Y - centerY;
            double angle = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
            if (angle < 0) angle += 360;
            cal.TwentyAngle = angle;
        }
        
        cal.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        
        _logger.LogInformation("Marked 20 for {CameraId} at angle {Angle}", cameraId, cal.TwentyAngle);

        return ToDto(cal);
    }
    
    private class Mark20Response
    {
        public string CameraId { get; set; } = "";
        public int Segment20Index { get; set; }
        public double TwentyAngle { get; set; }
    }

    /// <summary>
    /// Delete calibration for a camera
    /// </summary>
    [HttpDelete("{cameraId}")]
    public async Task<IActionResult> Delete(string cameraId)
    {
        var cal = await _db.Calibrations.FirstOrDefaultAsync(c => c.CameraId == cameraId);
        if (cal == null)
            return NotFound();

        // Delete files if they exist
        if (!string.IsNullOrEmpty(cal.CalibrationImagePath))
        {
            var path = Path.Combine(_env.WebRootPath, cal.CalibrationImagePath.TrimStart('/'));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        if (!string.IsNullOrEmpty(cal.OverlayImagePath))
        {
            var path = Path.Combine(_env.WebRootPath, cal.OverlayImagePath.TrimStart('/'));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }

        _db.Calibrations.Remove(cal);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static CalibrationDto ToDto(CalibrationEntity entity) => new()
    {
        CameraId = entity.CameraId,
        CalibrationImagePath = entity.CalibrationImagePath,
        OverlayImagePath = entity.OverlayImagePath,
        Quality = entity.Quality,
        TwentyAngle = entity.TwentyAngle,
        CalibrationData = entity.CalibrationData,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}

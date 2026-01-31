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
    private readonly string _calibrationDir;

    public CalibrationsController(DartsMobDbContext db, ILogger<CalibrationsController> logger, IWebHostEnvironment env)
    {
        _db = db;
        _logger = logger;
        _env = env;
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

        // Calculate angle from click position (assuming center is 0.5, 0.5)
        double centerX = 0.5;
        double centerY = 0.5;
        double dx = request.X - centerX;
        double dy = request.Y - centerY;
        
        // Angle in radians, converted to degrees
        // Dartboard: 20 is at the top, angles go clockwise
        double angle = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
        if (angle < 0) angle += 360;
        
        cal.TwentyAngle = angle;
        cal.UpdatedAt = DateTime.UtcNow;
        
        await _db.SaveChangesAsync();
        
        _logger.LogInformation("Marked 20 for {CameraId} at angle {Angle}", cameraId, angle);

        return ToDto(cal);
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

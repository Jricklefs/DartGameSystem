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

    public CalibrationsController(DartsMobDbContext db, ILogger<CalibrationsController> logger)
    {
        _db = db;
        _logger = logger;
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
    /// Get just the overlay image for a camera (for fast loading)
    /// </summary>
    [HttpGet("{cameraId}/overlay")]
    public async Task<IActionResult> GetOverlay(string cameraId)
    {
        var cal = await _db.Calibrations
            .Where(c => c.CameraId == cameraId)
            .Select(c => new { c.OverlayImage })
            .FirstOrDefaultAsync();
        
        if (cal?.OverlayImage == null)
            return NotFound();
        
        return File(cal.OverlayImage, "image/png");
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

        // Update fields
        if (!string.IsNullOrEmpty(dto.CalibrationImage))
            existing.CalibrationImage = Convert.FromBase64String(dto.CalibrationImage);
        
        if (!string.IsNullOrEmpty(dto.OverlayImage))
            existing.OverlayImage = Convert.FromBase64String(dto.OverlayImage);
        
        existing.Quality = dto.Quality;
        existing.TwentyAngle = dto.TwentyAngle;
        existing.CalibrationData = dto.CalibrationData;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        
        _logger.LogInformation("Saved calibration for {CameraId}, quality: {Quality}", 
            dto.CameraId, dto.Quality);

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

        _db.Calibrations.Remove(cal);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static CalibrationDto ToDto(CalibrationEntity entity) => new()
    {
        CameraId = entity.CameraId,
        CalibrationImage = entity.CalibrationImage.Length > 0 
            ? Convert.ToBase64String(entity.CalibrationImage) 
            : null,
        OverlayImage = entity.OverlayImage != null 
            ? Convert.ToBase64String(entity.OverlayImage) 
            : null,
        Quality = entity.Quality,
        TwentyAngle = entity.TwentyAngle,
        CalibrationData = entity.CalibrationData,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}

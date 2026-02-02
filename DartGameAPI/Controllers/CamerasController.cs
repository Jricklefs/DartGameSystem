using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Data;
using DartGameAPI.Hubs;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/boards/{boardId}/cameras")]
public class CamerasController : ControllerBase
{
    private readonly DartsMobDbContext _db;
    private readonly ILogger<CamerasController> _logger;

    public CamerasController(DartsMobDbContext db, ILogger<CamerasController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// List all cameras for a board
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<CameraDto>>> GetCameras(string boardId)
    {
        var cameras = await _db.Cameras
            .Where(c => c.BoardId == boardId && c.IsActive)
            .OrderBy(c => c.DeviceIndex)
            .Select(c => new CameraDto
            {
                CameraId = c.CameraId,
                DeviceIndex = c.DeviceIndex,
                DisplayName = c.DisplayName,
                IsCalibrated = c.IsCalibrated,
                CalibrationQuality = c.CalibrationQuality,
                LastCalibration = c.LastCalibration
            })
            .ToListAsync();

        return Ok(cameras);
    }

    /// <summary>
    /// Register a new camera for a board
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CameraDto>> RegisterCamera(string boardId, [FromBody] RegisterCameraRequest request)
    {
        // Verify board exists
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId && b.IsActive);
        if (board == null)
            return NotFound(new { error = "Board not found" });

        // Check if camera already exists
        var existing = await _db.Cameras.FirstOrDefaultAsync(
            c => c.BoardId == boardId && c.CameraId == request.CameraId);
        
        if (existing != null)
        {
            // Reactivate if soft-deleted
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                existing.DeviceIndex = request.DeviceIndex;
                existing.DisplayName = request.DisplayName;
                existing.IsCalibrated = false;
                existing.CalibrationQuality = null;
                existing.LastCalibration = null;
                await _db.SaveChangesAsync();
                
                _logger.LogInformation("Camera {CameraId} reactivated for board {BoardId}", 
                    request.CameraId, boardId);
            }
            else
            {
                return Conflict(new { error = $"Camera {request.CameraId} already registered" });
            }
        }
        else
        {
            var camera = new CameraEntity
            {
                CameraId = request.CameraId,
                BoardId = boardId,
                DeviceIndex = request.DeviceIndex,
                DisplayName = request.DisplayName ?? $"Camera {request.DeviceIndex}",
                IsCalibrated = false,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _db.Cameras.Add(camera);
            await _db.SaveChangesAsync();
            
            // Update board camera count
            board.CameraCount = await _db.Cameras.CountAsync(c => c.BoardId == boardId && c.IsActive);
            await _db.SaveChangesAsync();
            
            _logger.LogInformation("Camera {CameraId} registered for board {BoardId}", 
                request.CameraId, boardId);
        }

        return Ok(new CameraDto
        {
            CameraId = request.CameraId,
            DeviceIndex = request.DeviceIndex,
            DisplayName = request.DisplayName ?? $"Camera {request.DeviceIndex}",
            IsCalibrated = false,
            CalibrationQuality = null,
            LastCalibration = null
        });
    }

    /// <summary>
    /// Remove a camera from a board (soft delete)
    /// </summary>
    [HttpDelete("{cameraId}")]
    public async Task<ActionResult> RemoveCamera(string boardId, string cameraId)
    {
        var camera = await _db.Cameras.FirstOrDefaultAsync(
            c => c.BoardId == boardId && c.CameraId == cameraId && c.IsActive);
        
        if (camera == null)
            return NotFound(new { error = "Camera not found" });

        camera.IsActive = false;
        await _db.SaveChangesAsync();
        
        // Update board camera count and calibration status
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId);
        if (board != null)
        {
            board.CameraCount = await _db.Cameras.CountAsync(c => c.BoardId == boardId && c.IsActive);
            // Recalculate IsCalibrated (all remaining cameras must be calibrated)
            var allCalibrated = await _db.Cameras
                .Where(c => c.BoardId == boardId && c.IsActive)
                .AllAsync(c => c.IsCalibrated);
            board.IsCalibrated = allCalibrated && board.CameraCount > 0;
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Camera {CameraId} removed from board {BoardId}", cameraId, boardId);
        return Ok(new { message = $"Camera {cameraId} removed" });
    }

    /// <summary>
    /// Update camera calibration status (called after successful calibration)
    /// </summary>
    [HttpPut("{cameraId}/calibration")]
    public async Task<ActionResult> UpdateCameraCalibration(
        string boardId, 
        string cameraId, 
        [FromBody] UpdateCameraCalibrationRequest request)
    {
        var camera = await _db.Cameras.FirstOrDefaultAsync(
            c => c.BoardId == boardId && c.CameraId == cameraId && c.IsActive);
        
        if (camera == null)
            return NotFound(new { error = "Camera not found" });

        camera.IsCalibrated = request.IsCalibrated;
        camera.CalibrationQuality = request.Quality;
        camera.LastCalibration = request.IsCalibrated ? DateTime.UtcNow : camera.LastCalibration;
        await _db.SaveChangesAsync();

        // Update board calibration status (all cameras must be calibrated)
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId);
        if (board != null)
        {
            var allCalibrated = await _db.Cameras
                .Where(c => c.BoardId == boardId && c.IsActive)
                .AllAsync(c => c.IsCalibrated);
            board.IsCalibrated = allCalibrated && board.CameraCount > 0;
            board.LastCalibration = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Camera {CameraId} calibration updated: {Status} (quality: {Quality})", 
            cameraId, request.IsCalibrated ? "calibrated" : "not calibrated", request.Quality);
        
        return Ok(new { message = "Calibration updated" });
    }

    /// <summary>
    /// Get calibration status summary for a board
    /// </summary>
    [HttpGet("/api/boards/{boardId}/calibration-status")]
    public async Task<ActionResult<CalibrationStatusDto>> GetCalibrationStatus(string boardId)
    {
        var board = await _db.Boards.FirstOrDefaultAsync(b => b.BoardId == boardId && b.IsActive);
        if (board == null)
            return NotFound(new { error = "Board not found" });

        var cameras = await _db.Cameras
            .Where(c => c.BoardId == boardId && c.IsActive)
            .Select(c => new CameraCalibractionStatusDto
            {
                CameraId = c.CameraId,
                IsCalibrated = c.IsCalibrated,
                Quality = c.CalibrationQuality,
                LastCalibration = c.LastCalibration
            })
            .ToListAsync();

        var sensorConnected = GameHub.IsSensorConnected(boardId);

        return Ok(new CalibrationStatusDto
        {
            BoardId = boardId,
            CameraCount = cameras.Count,
            CalibratedCount = cameras.Count(c => c.IsCalibrated),
            AllCalibrated = cameras.All(c => c.IsCalibrated) && cameras.Count > 0,
            SensorConnected = sensorConnected,
            CanStartGame = cameras.All(c => c.IsCalibrated) && cameras.Count > 0 && sensorConnected,
            Cameras = cameras,
            Issues = GetIssues(cameras, sensorConnected)
        });
    }

    private List<string> GetIssues(List<CameraCalibractionStatusDto> cameras, bool sensorConnected)
    {
        var issues = new List<string>();
        
        if (cameras.Count == 0)
            issues.Add("No cameras registered");
        
        var uncalibrated = cameras.Where(c => !c.IsCalibrated).Select(c => c.CameraId).ToList();
        if (uncalibrated.Any())
            issues.Add($"Cameras not calibrated: {string.Join(", ", uncalibrated)}");
        
        if (!sensorConnected)
            issues.Add("Sensor not connected");
        
        return issues;
    }
}

// === DTOs ===

public class CameraDto
{
    public string CameraId { get; set; } = string.Empty;
    public int DeviceIndex { get; set; }
    public string? DisplayName { get; set; }
    public bool IsCalibrated { get; set; }
    public double? CalibrationQuality { get; set; }
    public DateTime? LastCalibration { get; set; }
}

public class RegisterCameraRequest
{
    public string CameraId { get; set; } = string.Empty;  // e.g. "cam0"
    public int DeviceIndex { get; set; }                  // USB device index
    public string? DisplayName { get; set; }              // Optional friendly name
}

public class UpdateCameraCalibrationRequest
{
    public bool IsCalibrated { get; set; }
    public double? Quality { get; set; }
}

public class CalibrationStatusDto
{
    public string BoardId { get; set; } = string.Empty;
    public int CameraCount { get; set; }
    public int CalibratedCount { get; set; }
    public bool AllCalibrated { get; set; }
    public bool SensorConnected { get; set; }
    public bool CanStartGame { get; set; }
    public List<CameraCalibractionStatusDto> Cameras { get; set; } = new();
    public List<string> Issues { get; set; } = new();
}

public class CameraCalibractionStatusDto
{
    public string CameraId { get; set; } = string.Empty;
    public bool IsCalibrated { get; set; }
    public double? Quality { get; set; }
    public DateTime? LastCalibration { get; set; }
}

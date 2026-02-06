using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Data;
using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Client for communicating with DartDetect API (fully stateless).
/// 
/// DartGame API is the HUB. DartSensor sends images here,
/// we include calibration data and forward to DartDetect for scoring.
/// </summary>
public class DartDetectClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DartDetectClient> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _baseUrl;

    public DartDetectClient(
        HttpClient httpClient, 
        IConfiguration config, 
        ILogger<DartDetectClient> logger,
        IServiceProvider serviceProvider)
    {
        _httpClient = httpClient;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _baseUrl = config["DartDetectApi:BaseUrl"] ?? "http://localhost:8000";
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Send images to DartDetect for dart tip detection and scoring.
    /// Includes calibration data with each camera (fully stateless).
    /// </summary>
    public async Task<DetectResponse?> DetectAsync(List<CameraImageDto> images, string boardId = "default", int dartNumber = 1, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Get calibration data for each camera from DB
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();
            
            var camerasWithCalibration = new List<object>();
            
            foreach (var img in images)
            {
                // Look up calibration for this camera
                var calibration = await db.Calibrations
                    .Where(c => c.CameraId == img.CameraId)
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                
                object? calibrationData = null;
                if (calibration?.CalibrationData != null)
                {
                    try
                    {
                        calibrationData = JsonSerializer.Deserialize<object>(calibration.CalibrationData);
                        _logger.LogDebug("Loaded calibration for camera {CameraId} (quality: {Quality})", 
                            img.CameraId, calibration.Quality);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse calibration data for camera {CameraId}", img.CameraId);
                    }
                }
                else
                {
                    _logger.LogWarning("No calibration found for camera {CameraId}", img.CameraId);
                }
                
                camerasWithCalibration.Add(new
                {
                    camera_id = img.CameraId,
                    image = img.Image,
                    calibration = calibrationData
                });
            }
            
            _logger.LogInformation("[TIMING] DB calibration fetch: {ElapsedMs}ms", sw.ElapsedMilliseconds);
            var httpStart = sw.ElapsedMilliseconds;
            
            var payload = new { 
                cameras = camerasWithCalibration,
                board_id = boardId,
                dart_number = dartNumber
            };
            
            _logger.LogInformation("[DETECT] Sending {Count} images with calibration to DartDetect (board={BoardId}, dart={DartNum})", 
                images.Count, boardId, dartNumber);
            
            var response = await _httpClient.PostAsJsonAsync("/v1/detect", payload, ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DetectResponse>(cancellationToken: ct);
                var httpTime = sw.ElapsedMilliseconds - httpStart;
                _logger.LogInformation("[TIMING] HTTP call to DartDetect: {HttpMs}ms, TOTAL: {TotalMs}ms", httpTime, sw.ElapsedMilliseconds);
                _logger.LogInformation("[DETECT] DartDetect returned {Count} tips", result?.Tips?.Count ?? 0);
                return result;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[DETECT] DartDetect failed: {Status} - {Error}", response.StatusCode, error);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DETECT] Failed to call DartDetect /v1/detect");
            return null;
        }
    }

    /// <summary>
    /// Tell DartDetect to capture new baseline images (no-op in stateless mode).
    /// </summary>
    public void RebaseFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Calling DartDetect /rebase (fire-and-forget)");
                await _httpClient.PostAsync("/rebase", null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebase DartDetect");
            }
        });
    }

    /// <summary>
    /// Health check for DartDetect service.
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

// Simple DTO for input - uses existing models for response
public class CameraImageDto
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
}

public class DartDetectException : Exception
{
    public DartDetectException(string message) : base(message) { }
    public DartDetectException(string message, Exception inner) : base(message, inner) { }
}

public class RebaseResult
{
    public string? Message { get; set; }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IMemoryCache _cache;
    private readonly string _baseUrl;
    private static readonly TimeSpan CalibrationCacheTtl = TimeSpan.FromSeconds(30);

    public DartDetectClient(
        HttpClient httpClient, 
        IConfiguration config, 
        ILogger<DartDetectClient> logger,
        IServiceProvider serviceProvider,
        IMemoryCache cache)
    {
        _httpClient = httpClient;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _cache = cache;
        _baseUrl = config["DartDetectApi:BaseUrl"] ?? "http://localhost:8000";
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        
        // Keep-alive disabled - was not helping with latency
        // StartKeepAlive();
    }
    
    private System.Threading.Timer? _keepAliveTimer;
    
    private void StartKeepAlive()
    {
        _keepAliveTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                // Ping DartDetect health endpoint every 30 seconds
                var resp = await _httpClient.GetAsync("/health");
                _logger.LogDebug("[KEEPALIVE] DartDetect ping: {Status}", resp.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[KEEPALIVE] DartDetect ping failed: {Error}", ex.Message);
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Send images to DartDetect for dart tip detection and scoring.
    /// Includes calibration data with each camera (fully stateless).
    /// </summary>
    /// <summary>
    /// Send images to DartDetect using multipart/form-data (saves ~33% bandwidth).
    /// 
    /// Instead of base64-encoding images in JSON (which inflates data by ~33%),
    /// we send raw JPEG bytes as file uploads. Metadata (board_id, dart_number,
    /// calibrations) goes as a JSON form field.
    /// 
    /// Falls back to the old JSON endpoint if multipart fails.
    /// </summary>
    public async Task<DetectResponse?> DetectAsync(List<CameraImageDto> images, string boardId = "default", int dartNumber = 1, List<CameraImageDto>? beforeImages = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Get calibration data for each camera from DB
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();
            
            // Build calibration map for metadata JSON
            var calibrations = new Dictionary<string, object?>();
            foreach (var img in images)
            {
                var calibrationData = await GetCalibrationDataAsync(db, img.CameraId, ct);
                calibrations[img.CameraId] = calibrationData;
            }
            
            _logger.LogInformation("[TIMING] DB calibration fetch: {ElapsedMs}ms", sw.ElapsedMilliseconds);
            var httpStart = sw.ElapsedMilliseconds;
            
            // === MULTIPART/FORM-DATA ===
            // Send raw image bytes instead of base64 strings.
            // This saves ~33% bandwidth since base64 inflates binary data.
            using var formContent = new MultipartFormDataContent();
            
            // Metadata as JSON form field (board_id, dart_number, per-camera calibrations)
            var metadata = new
            {
                board_id = boardId,
                dart_number = dartNumber,
                cameras = calibrations
            };
            var metadataJson = JsonSerializer.Serialize(metadata);
            formContent.Add(new StringContent(metadataJson, System.Text.Encoding.UTF8, "application/json"), "metadata");
            
            // Camera images as raw byte file uploads (field name: camera_<cameraId>)
            long totalRawBytes = 0;
            long totalBase64Bytes = 0;
            foreach (var img in images)
            {
                // Decode base64 to raw bytes — DartSensor sends base64, we convert to raw for wire savings
                var rawBytes = Convert.FromBase64String(img.Image);
                totalRawBytes += rawBytes.Length;
                totalBase64Bytes += img.Image.Length;
                
                var fileContent = new ByteArrayContent(rawBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                formContent.Add(fileContent, $"camera_{img.CameraId}", $"{img.CameraId}.jpg");
            }
            
            // Before images as raw byte file uploads (field name: before_<cameraId>)
            if (beforeImages != null && beforeImages.Any())
            {
                foreach (var img in beforeImages)
                {
                    var rawBytes = Convert.FromBase64String(img.Image);
                    totalRawBytes += rawBytes.Length;
                    totalBase64Bytes += img.Image.Length;
                    
                    var fileContent = new ByteArrayContent(rawBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    formContent.Add(fileContent, $"before_{img.CameraId}", $"before_{img.CameraId}.jpg");
                }
                _logger.LogInformation("[DETECT] Including {Count} before images for clean diff", beforeImages.Count);
            }
            
            var savedBytes = totalBase64Bytes - totalRawBytes;
            _logger.LogInformation("[DETECT] Sending {Count} images via multipart to DartDetect (board={BoardId}, dart={DartNum}, saved {SavedKB}KB)", 
                images.Count, boardId, dartNumber, savedBytes / 1024);
            
            var response = await _httpClient.PostAsync("/v1/detect/multipart", formContent, ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DetectResponse>(cancellationToken: ct);
                var httpTime = sw.ElapsedMilliseconds - httpStart;
                _logger.LogInformation("[TIMING] HTTP multipart call to DartDetect: {HttpMs}ms, TOTAL: {TotalMs}ms", httpTime, sw.ElapsedMilliseconds);
                _logger.LogInformation("[DETECT] DartDetect returned {Count} tips", result?.Tips?.Count ?? 0);
                return result;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[DETECT] DartDetect multipart failed: {Status} - {Error}, falling back to JSON", response.StatusCode, error);
                
                // Fallback to old JSON endpoint for backward compatibility
                return await DetectAsyncJsonFallback(images, calibrations, boardId, dartNumber, beforeImages, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DETECT] Failed to call DartDetect /v1/detect/multipart");
            return null;
        }
    }
    
    /// <summary>
    /// Fallback: send detection request via JSON+base64 (old method).
    /// Used if the multipart endpoint is unavailable (e.g., older DartDetect version).
    /// </summary>
    private async Task<DetectResponse?> DetectAsyncJsonFallback(
        List<CameraImageDto> images, 
        Dictionary<string, object?> calibrations,
        string boardId, int dartNumber,
        List<CameraImageDto>? beforeImages,
        CancellationToken ct)
    {
        try
        {
            var camerasWithCalibration = images.Select(img => new DetectCameraPayload
            {
                CameraId = img.CameraId,
                Image = img.Image,
                Calibration = calibrations.GetValueOrDefault(img.CameraId)
            }).ToList();
            
            List<BeforeImagePayload>? beforePayload = beforeImages?.Select(i => new BeforeImagePayload
            {
                CameraId = i.CameraId,
                Image = i.Image
            }).ToList();
            
            var payload = new DetectRequestPayload
            {
                Cameras = camerasWithCalibration,
                BeforeImages = beforePayload,
                BoardId = boardId,
                DartNumber = dartNumber
            };
            
            _logger.LogInformation("[DETECT] JSON fallback: sending {Count} images to /v1/detect", images.Count);
            var response = await _httpClient.PostAsJsonAsync("/v1/detect", payload, ct);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DetectResponse>(cancellationToken: ct);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[DETECT] JSON fallback also failed: {Status} - {Error}", response.StatusCode, error);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DETECT] JSON fallback failed");
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

    private async Task<object?> GetCalibrationDataAsync(DartsMobDbContext db, string cameraId, CancellationToken ct)
    {
        var cacheKey = $"calibration:{cameraId}";
        if (_cache.TryGetValue(cacheKey, out object? cached))
        {
            return cached;
        }

        var calibration = await db.Calibrations
            .Where(c => c.CameraId == cameraId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        object? calibrationData = null;
        if (calibration?.CalibrationData != null)
        {
            try
            {
                calibrationData = JsonSerializer.Deserialize<object>(calibration.CalibrationData);
                _logger.LogDebug("Loaded calibration for camera {CameraId} (quality: {Quality})",
                    cameraId, calibration.Quality);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse calibration data for camera {CameraId}", cameraId);
            }
        }
        else
        {
            _logger.LogWarning("No calibration found for camera {CameraId}", cameraId);
        }

        _cache.Set(cacheKey, calibrationData, CalibrationCacheTtl);
        return calibrationData;
    }
}

// Simple DTO for input - uses existing models for response
public class CameraImageDto
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
}

public class DetectCameraPayload
{
    [JsonPropertyName("camera_id")]
    public string CameraId { get; set; } = string.Empty;
    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;
    [JsonPropertyName("calibration")]
    public object? Calibration { get; set; }
}

public class DetectRequestPayload
{
    [JsonPropertyName("cameras")]
    public List<DetectCameraPayload> Cameras { get; set; } = new();
    [JsonPropertyName("before_images")]
    public List<BeforeImagePayload>? BeforeImages { get; set; }  // Frames before dart
    [JsonPropertyName("board_id")]
    public string BoardId { get; set; } = "default";
    [JsonPropertyName("dart_number")]
    public int DartNumber { get; set; } = 1;
}

public class BeforeImagePayload
{
    [JsonPropertyName("camera_id")]
    public string CameraId { get; set; } = string.Empty;
    [JsonPropertyName("image")]
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

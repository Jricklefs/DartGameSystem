using System.Net.Http.Json;
using System.Text.Json;

namespace DartGameAPI.Services;

/// <summary>
/// Client for communicating with DartDetect API (hub-and-spoke model).
/// 
/// DartGame API is the HUB. DartSensor sends images here,
/// we forward to DartDetect for scoring, then process the result.
/// </summary>
public class DartDetectClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DartDetectClient> _logger;
    private readonly string _baseUrl;

    public DartDetectClient(HttpClient httpClient, IConfiguration config, ILogger<DartDetectClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = config["DartDetectApi:BaseUrl"] ?? "http://localhost:8000";
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Send images to DartDetect for dart tip detection and scoring.
    /// This is the main detection endpoint - hub forwards sensor images here.
    /// </summary>
    public async Task<DetectResult?> DetectAsync(List<CameraImageDto> images, CancellationToken ct = default)
    {
        try
        {
            // DartDetect expects snake_case: { cameras: [{ camera_id, image }] }
            var payload = new { 
                cameras = images.Select(i => new { 
                    camera_id = i.CameraId, 
                    image = i.Image 
                }).ToList() 
            };
            
            _logger.LogDebug("Sending {Count} images to DartDetect for detection", images.Count);
            
            var response = await _httpClient.PostAsJsonAsync("/v1/detect", payload, ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DetectResult>(cancellationToken: ct);
                _logger.LogDebug("DartDetect returned {Count} tips", result?.Tips?.Count ?? 0);
                return result;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("DartDetect detection failed: {Status} - {Error}", response.StatusCode, error);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call DartDetect /v1/detect");
            return null;
        }
    }

    /// <summary>
    /// Tell DartDetect to capture new baseline images.
    /// Fire-and-forget - don't wait for response.
    /// </summary>
    public void RebaseFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Calling DartDetect /rebase (fire-and-forget)");
                var response = await _httpClient.PostAsync("/rebase", null);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("DartDetect rebase completed");
                }
                else
                {
                    _logger.LogWarning("DartDetect rebase failed: {Status}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebase DartDetect");
            }
        });
    }

    /// <summary>
    /// Tell DartDetect to capture new baseline images (async version).
    /// </summary>
    public async Task<RebaseResult> RebaseAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Calling DartDetect /rebase");
            
            var response = await _httpClient.PostAsync("/rebase", null, ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<RebaseResult>(cancellationToken: ct);
                _logger.LogInformation("DartDetect rebase successful");
                return result ?? new RebaseResult { Message = "Success" };
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("DartDetect rebase failed: {Status} - {Error}", response.StatusCode, error);
                throw new DartDetectException($"Rebase failed: {response.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to DartDetect");
            throw new DartDetectException($"Cannot connect to DartDetect: {ex.Message}", ex);
        }
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

// === DTOs ===

public class CameraImageDto
{
    public string CameraId { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;  // Base64 encoded
}

public class DetectResult
{
    public List<DetectedTipDto> Tips { get; set; } = new();
}

public class DetectedTipDto
{
    public string CameraId { get; set; } = string.Empty;
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public string Zone { get; set; } = string.Empty;
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double Confidence { get; set; }
}

public class RebaseResult
{
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, bool>? Cameras { get; set; }
    public int KnownDartsCount { get; set; }
}

public class DartDetectException : Exception
{
    public DartDetectException(string message) : base(message) { }
    public DartDetectException(string message, Exception inner) : base(message, inner) { }
}

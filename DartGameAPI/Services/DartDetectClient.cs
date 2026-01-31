using System.Net.Http.Json;
using System.Text.Json;
using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Client for DartDetect API
/// </summary>
public class DartDetectClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DartDetectClient> _logger;
    private readonly string _apiKey;

    public DartDetectClient(HttpClient httpClient, IConfiguration config, ILogger<DartDetectClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        var baseUrl = config["DartDetectApi:BaseUrl"] ?? "http://localhost:8000";
        _apiKey = config["DartDetectApi:ApiKey"] ?? "";
        
        _httpClient.BaseAddress = new Uri(baseUrl);
        
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    /// <summary>
    /// Calibrate cameras
    /// </summary>
    public async Task<CalibrateResponse?> CalibrateAsync(List<CameraImage> cameras)
    {
        try
        {
            var request = new CalibrateRequest { Cameras = cameras };
            var response = await _httpClient.PostAsJsonAsync("/v1/calibrate", request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<CalibrateResponse>();
            }
            
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Calibration failed: {StatusCode} - {Error}", response.StatusCode, error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling DartDetect calibrate");
            return null;
        }
    }

    /// <summary>
    /// Detect dart tips in images
    /// </summary>
    public async Task<DetectResponse?> DetectAsync(List<CameraImage> cameras)
    {
        try
        {
            var request = new DetectRequest { Cameras = cameras };
            var response = await _httpClient.PostAsJsonAsync("/v1/detect", request);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DetectResponse>();
            }
            
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Detection failed: {StatusCode} - {Error}", response.StatusCode, error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling DartDetect detect");
            return null;
        }
    }

    /// <summary>
    /// Health check
    /// </summary>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

using System.Net.Http.Json;

namespace DartGameAPI.Services;

/// <summary>
/// Client for communicating with DartSensor API.
/// DartSensor runs on each board and handles camera/motion detection.
/// </summary>
public class DartSensorClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DartSensorClient> _logger;
    private readonly string _baseUrl;

    public DartSensorClient(HttpClient httpClient, IConfiguration config, ILogger<DartSensorClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = config["DartSensorApi:BaseUrl"] ?? "http://localhost:8001";
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Tell sensor to start detecting (game started).
    /// Triggers baseline capture.
    /// </summary>
    public void StartGameFireAndForget(string boardId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Telling DartSensor to start game for board {BoardId}", boardId);
                var response = await _httpClient.PostAsync("/start", null);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("DartSensor started successfully");
                }
                else
                {
                    _logger.LogWarning("DartSensor start failed: {Status}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start DartSensor");
            }
        });
    }

    /// <summary>
    /// Tell sensor to stop detecting (game ended).
    /// </summary>
    public void StopGameFireAndForget(string boardId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Telling DartSensor to stop for board {BoardId}", boardId);
                await _httpClient.PostAsync("/stop", null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop DartSensor");
            }
        });
    }

    /// <summary>
    /// Tell sensor to capture new baseline (board cleared).
    /// </summary>
    public void RebaseFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Telling DartSensor to rebase");
                await _httpClient.PostAsync("/rebase", null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebase DartSensor");
            }
        });
    }

    /// <summary>
    /// Get sensor status.
    /// </summary>
    public async Task<SensorStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/status", ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SensorStatus>(cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get sensor status");
        }
        return null;
    }

    /// <summary>
    /// Health check for sensor.
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

public class SensorStatus
{
    public bool GameStarted { get; set; }
    public int DartCount { get; set; }
    public int Cameras { get; set; }
    public int BaselinesCaptured { get; set; }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Models;
using DartGameAPI.Hubs;
using DartGameAPI.Data;

namespace DartGameAPI.Services;

public class DartSensorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly ILogger<DartSensorService> _logger;
    private readonly string _dartDetectBaseUrl;
    private readonly int _pollIntervalMs;

    public DartSensorService(IServiceScopeFactory scopeFactory, IHubContext<GameHub> hubContext, IConfiguration config, ILogger<DartSensorService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
        _dartDetectBaseUrl = config["DartDetectApi:BaseUrl"] ?? "http://localhost:8000";
        _pollIntervalMs = config.GetValue("DartSensor:PollIntervalMs", 100);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DartSensorService starting...");
        await Task.Delay(2000, stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var gameService = scope.ServiceProvider.GetRequiredService<GameService>();
                var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();
                var activeGame = gameService.GetGameForBoard("default");
                
                if (activeGame != null && activeGame.State == GameState.InProgress)
                {
                    await CheckForDartsAsync(gameService, db, activeGame, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in DartSensorService loop");
            }
            await Task.Delay(_pollIntervalMs, stoppingToken);
        }
    }

    private async Task CheckForDartsAsync(GameService gameService, DartsMobDbContext db, Game game, CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(_dartDetectBaseUrl) };
            
            var cameras = new List<CameraImage>();
            for (int i = 0; i < 3; i++)
            {
                var snapshot = await GetSnapshotAsync(httpClient, i, ct);
                if (snapshot != null) cameras.Add(new CameraImage { CameraId = $"cam{i}", Image = snapshot });
            }
            if (!cameras.Any()) return;

            // Get the rotation offset from calibration (Mark 20 feature)
            // Use the first camera's calibration for now
            double rotationOffsetDegrees = 0;
            var calibration = await db.Calibrations.FirstOrDefaultAsync(c => c.CameraId == "cam0", ct);
            if (calibration?.TwentyAngle != null)
            {
                rotationOffsetDegrees = calibration.TwentyAngle.Value;
            }

            var detectRequest = new 
            { 
                Cameras = cameras,
                RotationOffsetDegrees = rotationOffsetDegrees
            };

            var response = await httpClient.PostAsJsonAsync("/v1/detect", detectRequest, ct);
            if (!response.IsSuccessStatusCode) return;
            
            var detectResult = await response.Content.ReadFromJsonAsync<DetectResponse>(cancellationToken: ct);
            if (detectResult == null) return;

            foreach (var tip in detectResult.Tips.Where(t => t.Confidence > 0.5))
            {
                if (IsNewDart(game, tip))
                {
                    _logger.LogInformation("Dart detected: {Zone} = {Score}", tip.Zone, tip.Score);
                    
                    var dart = new DartThrow
                    {
                        Index = game.CurrentTurn?.Darts.Count ?? 0,
                        Segment = tip.Segment,
                        Multiplier = tip.Multiplier,
                        Zone = tip.Zone,
                        Score = tip.Score,
                        XMm = tip.XMm,
                        YMm = tip.YMm,
                        Confidence = tip.Confidence
                    };
                    
                    game.KnownDarts.Add(new KnownDart { XMm = tip.XMm, YMm = tip.YMm, Score = tip.Score, DetectedAt = DateTime.UtcNow });
                    gameService.ApplyManualDart(game, dart);
                    await _hubContext.SendDartThrown(game.BoardId, dart, game);
                    
                    if (game.State == GameState.Finished)
                        await _hubContext.SendGameEnded(game.BoardId, game);
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Error checking for darts"); }
    }

    private bool IsNewDart(Game game, DetectedTip tip)
    {
        foreach (var known in game.KnownDarts)
        {
            var dist = Math.Sqrt(Math.Pow(tip.XMm - known.XMm, 2) + Math.Pow(tip.YMm - known.YMm, 2));
            if (dist < 20) return false;
        }
        return true;
    }

    private async Task<string?> GetSnapshotAsync(HttpClient client, int camIdx, CancellationToken ct)
    {
        try
        {
            var resp = await client.GetAsync($"/cameras/{camIdx}/snapshot", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                return json.GetProperty("image").GetString();
            }
        }
        catch { }
        return null;
    }
}

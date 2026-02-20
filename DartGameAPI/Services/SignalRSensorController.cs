using Microsoft.AspNetCore.SignalR;
using DartGameAPI.Hubs;

namespace DartGameAPI.Services;

/// <summary>
/// Controls the dart sensor via SignalR hub commands.
/// Sends ResumeDetection/PauseDetection/Rebase to the connected DartSensor.
/// </summary>
public class SignalRSensorController : IDartSensorController
{
    private readonly IHubContext<GameHub> _hubContext;
    private readonly ILogger<SignalRSensorController> _logger;

    public SignalRSensorController(IHubContext<GameHub> hubContext, ILogger<SignalRSensorController> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task StartArm(string boardId)
    {
        _logger.LogInformation("Sensor StartArm: board {BoardId} - sending ResumeDetection + Rebase", boardId);
        await _hubContext.SendResumeDetection(boardId);
        await _hubContext.SendRebase(boardId);
    }

    public async Task PauseStop(string boardId)
    {
        _logger.LogInformation("Sensor PauseStop: board {BoardId} - sending PauseDetection", boardId);
        await _hubContext.SendPauseDetection(boardId);
    }

    public async Task ReArm(string boardId)
    {
        _logger.LogInformation("Sensor ReArm: board {BoardId} - sending ResumeDetection", boardId);
        await _hubContext.SendResumeDetection(boardId);
    }

    public async Task ReportError(string boardId, string error)
    {
        _logger.LogError("Sensor error on board {BoardId}: {Error}", boardId, error);
        // Notify UI clients about sensor error
        await _hubContext.Clients.Group($"board:{boardId}").SendAsync("SensorError", new { boardId, error });
    }
}

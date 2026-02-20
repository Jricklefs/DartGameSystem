namespace DartGameAPI.Services;

/// <summary>
/// Interface for controlling the dart sensor hardware.
/// Abstracts sensor commands so the engine doesn't depend on SignalR directly.
/// </summary>
public interface IDartSensorController
{
    /// <summary>Start detection and rebase (new turn starting)</summary>
    Task StartArm(string boardId);

    /// <summary>Pause/stop detection (bust confirmed, player pulling darts)</summary>
    Task PauseStop(string boardId);

    /// <summary>Re-arm detection (after bust override, continue turn)</summary>
    Task ReArm(string boardId);

    /// <summary>Report a sensor error</summary>
    Task ReportError(string boardId, string error);
}

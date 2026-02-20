using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Interface for dart detection - implemented by both native C++ and HTTP backends.
/// </summary>
public interface IDartDetectService
{
    /// <summary>
    /// Initialize with calibration data. Call on startup.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Detect a dart from camera images.
    /// </summary>
    Task<DetectResponse?> DetectAsync(
        List<CameraImageDto> images,
        string boardId = "default",
        int dartNumber = 1,
        List<CameraImageDto>? beforeImages = null,
        List<List<CameraImageDto>>? multiFrameImages = null,
        CancellationToken ct = default);

    /// <summary>
    /// Initialize board cache for a new game.
    /// </summary>
    void InitBoard(string boardId);

    /// <summary>
    /// Clear board cache (board cleared or game ended).
    /// </summary>
    void ClearBoard(string boardId);
}

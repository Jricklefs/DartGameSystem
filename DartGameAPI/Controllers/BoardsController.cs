using Microsoft.AspNetCore.Mvc;
using DartGameAPI.Models;
using DartGameAPI.Services;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BoardsController : ControllerBase
{
    private readonly GameService _gameService;

    public BoardsController(GameService gameService)
    {
        _gameService = gameService;
    }

    /// <summary>
    /// List all boards
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<Board>> GetBoards()
    {
        return Ok(_gameService.GetAllBoards());
    }

    /// <summary>
    /// Get a specific board
    /// </summary>
    [HttpGet("{id}")]
    public ActionResult<Board> GetBoard(string id)
    {
        var board = _gameService.GetBoard(id);
        if (board == null) return NotFound();
        return Ok(board);
    }

    /// <summary>
    /// Register a new board
    /// </summary>
    [HttpPost]
    public ActionResult<Board> RegisterBoard([FromBody] RegisterBoardRequest request)
    {
        _gameService.RegisterBoard(request.Id, request.Name, request.CameraIds);
        return Ok(_gameService.GetBoard(request.Id));
    }

    /// <summary>
    /// Clear darts from board (player pulled darts)
    /// </summary>
    [HttpPost("{id}/clear")]
    public ActionResult ClearBoard(string id)
    {
        _gameService.ClearBoard(id);
        return Ok(new { message = "Board cleared" });
    }
}

public class RegisterBoardRequest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> CameraIds { get; set; } = new();
}

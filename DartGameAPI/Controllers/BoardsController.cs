using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BoardsController : ControllerBase
{
    private readonly IConfiguration _config;

    public BoardsController(IConfiguration config)
    {
        _config = config;
    }

    private string GetConnectionString() => _config.GetConnectionString("DartsMobDB")
        ?? "Server=JOESSERVER2019;Database=DartsMobDB;User Id=DartsMobApp;Password=Stewart14s!2;TrustServerCertificate=True;";

    [HttpGet]
    public async Task<ActionResult> GetBoards()
    {
        var connStr = GetConnectionString();
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var boards = new List<object>();
        using var cmd = new SqlCommand("SELECT BoardId, Name, CreatedAt FROM Boards ORDER BY CreatedAt", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            boards.Add(new
            {
                id = reader.GetString(0),
                name = reader.GetString(1),
                createdAt = reader.GetDateTime(2)
            });
        }
        return Ok(boards);
    }

    [HttpGet("current")]
    public async Task<ActionResult> GetCurrentBoard()
    {
        var connStr = GetConnectionString();
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new SqlCommand("SELECT TOP 1 BoardId, Name FROM Boards ORDER BY CreatedAt", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return Ok(new { id = reader.GetString(0), name = reader.GetString(1) });
        }
        return NotFound("No board registered");
    }

    [HttpPost]
    public async Task<ActionResult> RegisterBoard([FromBody] RegisterBoardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest("Board name is required");

        var connStr = GetConnectionString();
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var boardId = Guid.NewGuid().ToString().ToUpper();
        using var cmd = new SqlCommand(
            "INSERT INTO Boards (BoardId, Name, CreatedAt) VALUES (@BoardId, @Name, GETUTCDATE())", conn);
        cmd.Parameters.AddWithValue("@BoardId", boardId);
        cmd.Parameters.AddWithValue("@Name", request.Name);
        await cmd.ExecuteNonQueryAsync();

        return Ok(new { id = boardId, name = request.Name });
    }

    [HttpPut("{boardId}")]
    public async Task<ActionResult> UpdateBoard(string boardId, [FromBody] UpdateBoardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest("Board name is required");

        var connStr = GetConnectionString();
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new SqlCommand("UPDATE Boards SET Name = @Name WHERE BoardId = @BoardId", conn);
        cmd.Parameters.AddWithValue("@BoardId", boardId);
        cmd.Parameters.AddWithValue("@Name", request.Name);
        var rows = await cmd.ExecuteNonQueryAsync();

        if (rows == 0) return NotFound("Board not found");
        return Ok(new { id = boardId, name = request.Name });
    }
}

public class RegisterBoardRequest
{
    public string Name { get; set; } = "";
}

public class UpdateBoardRequest
{
    public string Name { get; set; } = "";
}
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
}

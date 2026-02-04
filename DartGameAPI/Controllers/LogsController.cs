using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private readonly string _connectionString;

    public LogsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Server=JOESSERVER2019;Database=DartsMobDB;User Id=DartsMobApp;Password=Stewart14s!2;TrustServerCertificate=True;";
    }

    public class LogEntry
    {
        public string Source { get; set; } = "Unknown";
        public string Level { get; set; } = "INFO";
        public string? Category { get; set; }
        public string Message { get; set; } = "";
        public object? Data { get; set; }
        public Guid? GameId { get; set; }
        public int? PlayerId { get; set; }
    }

    public class LogRecord
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = "";
        public string Level { get; set; } = "";
        public string? Category { get; set; }
        public string Message { get; set; } = "";
        public string? Data { get; set; }
        public Guid? GameId { get; set; }
        public int? PlayerId { get; set; }
    }

    /// <summary>
    /// Add a log entry
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddLog([FromBody] LogEntry entry)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var dataJson = entry.Data != null ? JsonSerializer.Serialize(entry.Data) : null;

            var sql = @"INSERT INTO Logs (Source, Level, Category, Message, Data, GameId, PlayerId) 
                        VALUES (@Source, @Level, @Category, @Message, @Data, @GameId, @PlayerId);
                        SELECT SCOPE_IDENTITY();";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Source", entry.Source);
            cmd.Parameters.AddWithValue("@Level", entry.Level);
            cmd.Parameters.AddWithValue("@Category", (object?)entry.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Message", entry.Message);
            cmd.Parameters.AddWithValue("@Data", (object?)dataJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@GameId", (object?)entry.GameId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PlayerId", (object?)entry.PlayerId ?? DBNull.Value);

            var id = await cmd.ExecuteScalarAsync();
            return Ok(new { id = Convert.ToInt32(id) });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get logs with optional filters
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] int limit = 100,
        [FromQuery] string? source = null,
        [FromQuery] string? level = null,
        [FromQuery] string? category = null,
        [FromQuery] Guid? gameId = null)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = new StringBuilder("SELECT TOP (@Limit) * FROM Logs WHERE 1=1");
            
            if (!string.IsNullOrEmpty(source))
                sql.Append(" AND Source = @Source");
            if (!string.IsNullOrEmpty(level))
                sql.Append(" AND Level = @Level");
            if (!string.IsNullOrEmpty(category))
                sql.Append(" AND Category = @Category");
            if (gameId.HasValue)
                sql.Append(" AND GameId = @GameId");
            
            sql.Append(" ORDER BY Timestamp DESC");

            using var cmd = new SqlCommand(sql.ToString(), conn);
            cmd.Parameters.AddWithValue("@Limit", limit);
            if (!string.IsNullOrEmpty(source))
                cmd.Parameters.AddWithValue("@Source", source);
            if (!string.IsNullOrEmpty(level))
                cmd.Parameters.AddWithValue("@Level", level);
            if (!string.IsNullOrEmpty(category))
                cmd.Parameters.AddWithValue("@Category", category);
            if (gameId.HasValue)
                cmd.Parameters.AddWithValue("@GameId", gameId.Value);

            var logs = new List<LogRecord>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(new LogRecord
                {
                    Id = reader.GetInt32(0),
                    Timestamp = reader.GetDateTime(1),
                    Source = reader.GetString(2),
                    Level = reader.GetString(3),
                    Category = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Message = reader.GetString(5),
                    Data = reader.IsDBNull(6) ? null : reader.GetString(6),
                    GameId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    PlayerId = reader.IsDBNull(8) ? null : reader.GetInt32(8)
                });
            }

            return Ok(logs);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Download logs as text file
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> DownloadLogs([FromQuery] int limit = 1000)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = "SELECT TOP (@Limit) * FROM Logs ORDER BY Timestamp DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Limit", limit);

            var sb = new StringBuilder();
            sb.AppendLine("=== DartsMob Logs ===");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Entries: {limit} max");
            sb.AppendLine(new string('=', 50));
            sb.AppendLine();

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var timestamp = reader.GetDateTime(1).ToString("yyyy-MM-dd HH:mm:ss.fff");
                var source = reader.GetString(2);
                var level = reader.GetString(3);
                var category = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var message = reader.GetString(5);
                var data = reader.IsDBNull(6) ? "" : reader.GetString(6);

                sb.AppendLine($"[{timestamp}] [{source}] [{level}] {(category != "" ? $"[{category}] " : "")}{message}");
                if (!string.IsNullOrEmpty(data))
                    sb.AppendLine($"  Data: {data}");
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/plain", $"dartsmob-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clear all logs
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> ClearLogs()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand("DELETE FROM Logs", conn);
            var deleted = await cmd.ExecuteNonQueryAsync();

            return Ok(new { deleted = deleted, message = "Logs cleared" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get log count
    /// </summary>
    [HttpGet("count")]
    public async Task<IActionResult> GetLogCount()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand("SELECT COUNT(*) FROM Logs", conn);
            var count = (int)(await cmd.ExecuteScalarAsync() ?? 0);

            return Ok(new { count = count });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

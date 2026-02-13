using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackgroundsController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BackgroundsController> _logger;
    private readonly IConfiguration _config;

    public BackgroundsController(IWebHostEnvironment env, ILogger<BackgroundsController> logger, IConfiguration config)
    {
        _env = env;
        _logger = logger;
        _config = config;
    }

    private string GetConnectionString() => _config.GetConnectionString("DartsMobDB")
        ?? "Server=JOESSERVER2019;Database=DartsMobDB;User Id=DartsMobApp;Password=Stewart14s!2;TrustServerCertificate=True;";

    // ===== Background Selections (Server-Side Persistence) =====

    [HttpGet("selections")]
    public async Task<ActionResult> GetSelections()
    {
        try
        {
            var standard = new List<string>();
            var nsfw = new List<string>();

            using var conn = new SqlConnection(GetConnectionString());
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT ImagePath, IsNsfw FROM BackgroundSettings WHERE IsSelected = 1", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var path = reader.GetString(0);
                var isNsfw = reader.GetBoolean(1);
                if (isNsfw) nsfw.Add(path);
                else standard.Add(path);
            }

            return Ok(new { standard, nsfw });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting background selections");
            return StatusCode(500, "Error getting selections");
        }
    }

    public class SelectionsDto
    {
        public List<string> Standard { get; set; } = new();
        public List<string> Nsfw { get; set; } = new();
    }

    [HttpPost("selections")]
    public async Task<ActionResult> SaveSelections([FromBody] SelectionsDto dto)
    {
        try
        {
            using var conn = new SqlConnection(GetConnectionString());
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            using (var del = new SqlCommand("DELETE FROM BackgroundSettings", conn, tx))
                await del.ExecuteNonQueryAsync();

            foreach (var path in dto.Standard ?? new List<string>())
            {
                using var ins = new SqlCommand("INSERT INTO BackgroundSettings (ImagePath, IsSelected, IsNsfw) VALUES (@p, 1, 0)", conn, tx);
                ins.Parameters.AddWithValue("@p", path);
                await ins.ExecuteNonQueryAsync();
            }

            foreach (var path in dto.Nsfw ?? new List<string>())
            {
                using var ins = new SqlCommand("INSERT INTO BackgroundSettings (ImagePath, IsSelected, IsNsfw) VALUES (@p, 1, 1)", conn, tx);
                ins.Parameters.AddWithValue("@p", path);
                await ins.ExecuteNonQueryAsync();
            }

            tx.Commit();
            _logger.LogInformation("Saved {Standard} standard + {Nsfw} NSFW background selections", dto.Standard?.Count ?? 0, dto.Nsfw?.Count ?? 0);
            return Ok(new { saved = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving background selections");
            return StatusCode(500, "Error saving selections");
        }
    }

    // ===== Existing endpoints =====

    [HttpGet("nsfw")]
    public ActionResult<List<string>> GetNsfwBackgrounds()
    {
        try
        {
            var nsfwPath = Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw");
            if (!Directory.Exists(nsfwPath))
            {
                Directory.CreateDirectory(nsfwPath);
                return Ok(new List<string>());
            }
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var images = Directory.GetFiles(nsfwPath)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                .Select(f => $"/images/backgrounds/nsfw/{Path.GetFileName(f)}")
                .OrderBy(f => f)
                .ToList();
            _logger.LogInformation("Found {Count} NSFW backgrounds", images.Count);
            return Ok(images);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing NSFW backgrounds");
            return StatusCode(500, "Error listing backgrounds");
        }
    }

    [HttpGet("sfw")]
    public ActionResult<List<string>> GetSfwBackgrounds()
    {
        try
        {
            var bgPath = Path.Combine(_env.WebRootPath, "images", "backgrounds");
            if (!Directory.Exists(bgPath)) return Ok(new List<string>());
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var images = Directory.GetFiles(bgPath)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                .Select(f => $"/images/backgrounds/{Path.GetFileName(f)}")
                .OrderBy(f => f)
                .ToList();
            return Ok(images);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing SFW backgrounds");
            return StatusCode(500, "Error listing backgrounds");
        }
    }

    [HttpPost("nsfw/upload")]
    [RequestSizeLimit(10_000_000)]
    public async Task<ActionResult> UploadNsfwBackground(IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0) return BadRequest("No file provided");
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!extensions.Contains(ext)) return BadRequest("Invalid file type");
            var nsfwPath = Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw");
            Directory.CreateDirectory(nsfwPath);
            var safeName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Path.GetFileNameWithoutExtension(file.FileName)}{ext}";
            var filePath = Path.Combine(nsfwPath, safeName);
            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);
            _logger.LogInformation("Uploaded NSFW background: {Name}", safeName);
            return Ok(new { path = $"/images/backgrounds/nsfw/{safeName}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading NSFW background");
            return StatusCode(500, "Upload failed");
        }
    }

    [HttpDelete("nsfw/{filename}")]
    public ActionResult DeleteNsfwBackground(string filename)
    {
        try
        {
            if (filename.Contains("..") || filename.Contains("/") || filename.Contains("\\")) return BadRequest("Invalid filename");
            var filePath = Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw", filename);
            if (!System.IO.File.Exists(filePath)) return NotFound("File not found");
            System.IO.File.Delete(filePath);
            _logger.LogInformation("Deleted NSFW background: {Name}", filename);
            return Ok(new { deleted = filename });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting NSFW background");
            return StatusCode(500, "Delete failed");
        }
    }
}

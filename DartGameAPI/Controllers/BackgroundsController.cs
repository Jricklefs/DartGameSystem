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

    public class BackgroundItem
    {
        public string Path { get; set; } = "";
        public bool Selected { get; set; }
    }

    [HttpGet("selections")]
    public async Task<ActionResult> GetSelections()
    {
        try
        {
            var dbStandard = new List<(string path, int sort)>();
            var dbNsfw = new List<(string path, int sort)>();

            using var conn = new SqlConnection(GetConnectionString());
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT ImagePath, IsNsfw, SortOrder FROM BackgroundSettings WHERE IsSelected = 1 ORDER BY SortOrder", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var path = reader.GetString(0);
                var isNsfw = reader.GetBoolean(1);
                var sort = reader.GetInt32(2);
                if (isNsfw) dbNsfw.Add((path, sort));
                else dbStandard.Add((path, sort));
            }
            reader.Close();

            // Get all images on disk
            var sfwOnDisk = GetSfwImagePaths();
            var nsfwOnDisk = GetNsfwImagePaths();

            var selectedStandardPaths = new HashSet<string>(dbStandard.Select(x => x.path));
            var selectedNsfwPaths = new HashSet<string>(dbNsfw.Select(x => x.path));

            // Build ordered lists: selected first (by sort order), then unselected
            var standard = new List<BackgroundItem>();
            foreach (var item in dbStandard.OrderBy(x => x.sort))
                standard.Add(new BackgroundItem { Path = item.path, Selected = true });
            foreach (var path in sfwOnDisk.Where(p => !selectedStandardPaths.Contains(p)))
                standard.Add(new BackgroundItem { Path = path, Selected = false });

            var nsfw = new List<BackgroundItem>();
            foreach (var item in dbNsfw.OrderBy(x => x.sort))
                nsfw.Add(new BackgroundItem { Path = item.path, Selected = true });
            foreach (var path in nsfwOnDisk.Where(p => !selectedNsfwPaths.Contains(p)))
                nsfw.Add(new BackgroundItem { Path = path, Selected = false });

            return Ok(new { standard, nsfw });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting background selections");
            return StatusCode(500, "Error getting selections");
        }
    }

    private List<string> GetSfwImagePaths()
    {
        var bgPath = System.IO.Path.Combine(_env.WebRootPath, "images", "backgrounds");
        if (!Directory.Exists(bgPath)) return new List<string>();
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        return Directory.GetFiles(bgPath)
            .Where(f => extensions.Contains(System.IO.Path.GetExtension(f).ToLower()))
            .Select(f => "/images/backgrounds/" + System.IO.Path.GetFileName(f))
            .OrderBy(f => f)
            .ToList();
    }

    private List<string> GetNsfwImagePaths()
    {
        var nsfwPath = System.IO.Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw");
        if (!Directory.Exists(nsfwPath)) return new List<string>();
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        return Directory.GetFiles(nsfwPath)
            .Where(f => extensions.Contains(System.IO.Path.GetExtension(f).ToLower()))
            .Select(f => "/images/backgrounds/nsfw/" + System.IO.Path.GetFileName(f))
            .OrderBy(f => f)
            .ToList();
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

            for (int i = 0; i < (dto.Standard?.Count ?? 0); i++)
            {
                using var ins = new SqlCommand("INSERT INTO BackgroundSettings (ImagePath, IsSelected, IsNsfw, SortOrder) VALUES (@p, 1, 0, @s)", conn, tx);
                ins.Parameters.AddWithValue("@p", dto.Standard![i]);
                ins.Parameters.AddWithValue("@s", i);
                await ins.ExecuteNonQueryAsync();
            }

            for (int i = 0; i < (dto.Nsfw?.Count ?? 0); i++)
            {
                using var ins = new SqlCommand("INSERT INTO BackgroundSettings (ImagePath, IsSelected, IsNsfw, SortOrder) VALUES (@p, 1, 1, @s)", conn, tx);
                ins.Parameters.AddWithValue("@p", dto.Nsfw![i]);
                ins.Parameters.AddWithValue("@s", i);
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
            var images = GetNsfwImagePaths();
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
            return Ok(GetSfwImagePaths());
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
            var ext = System.IO.Path.GetExtension(file.FileName).ToLower();
            if (!extensions.Contains(ext)) return BadRequest("Invalid file type");
            var nsfwPath = System.IO.Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw");
            Directory.CreateDirectory(nsfwPath);
            var safeName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{System.IO.Path.GetFileNameWithoutExtension(file.FileName)}{ext}";
            var filePath = System.IO.Path.Combine(nsfwPath, safeName);
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
            var filePath = System.IO.Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw", filename);
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

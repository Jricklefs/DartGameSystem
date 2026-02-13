using Microsoft.AspNetCore.Mvc;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackgroundsController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BackgroundsController> _logger;

    public BackgroundsController(IWebHostEnvironment env, ILogger<BackgroundsController> logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Get list of NSFW background images
    /// </summary>
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

    /// <summary>
    /// Get list of SFW (default) background images
    /// </summary>
    [HttpGet("sfw")]
    public ActionResult<List<string>> GetSfwBackgrounds()
    {
        try
        {
            var bgPath = Path.Combine(_env.WebRootPath, "images", "backgrounds");
            
            if (!Directory.Exists(bgPath))
            {
                return Ok(new List<string>());
            }

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

    /// <summary>
    /// Upload NSFW background image
    /// </summary>
    [HttpPost("nsfw/upload")]
    [RequestSizeLimit(10_000_000)]
    public async Task<ActionResult> UploadNsfwBackground(IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file provided");

            var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!extensions.Contains(ext))
                return BadRequest("Invalid file type");

            var nsfwPath = Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw");
            Directory.CreateDirectory(nsfwPath);

            var safeName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Path.GetFileNameWithoutExtension(file.FileName)}{ext}";
            var filePath = Path.Combine(nsfwPath, safeName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            _logger.LogInformation("Uploaded NSFW background: {Name}", safeName);
            return Ok(new { path = $"/images/backgrounds/nsfw/{safeName}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading NSFW background");
            return StatusCode(500, "Upload failed");
        }
    }

    /// <summary>
    /// Delete NSFW background image
    /// </summary>
    [HttpDelete("nsfw/{filename}")]
    public ActionResult DeleteNsfwBackground(string filename)
    {
        try
        {
            if (filename.Contains("..") || filename.Contains("/") || filename.Contains("\\"))
                return BadRequest("Invalid filename");

            var filePath = Path.Combine(_env.WebRootPath, "images", "backgrounds", "nsfw", filename);

            if (!System.IO.File.Exists(filePath))
                return NotFound("File not found");

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

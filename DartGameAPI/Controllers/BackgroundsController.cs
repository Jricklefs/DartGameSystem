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
}

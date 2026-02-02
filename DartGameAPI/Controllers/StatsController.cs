using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DartGameAPI.Data;
using DartGameAPI.Models;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly DartsMobDbContext _db;

    public StatsController(DartsMobDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Get game records (highest avg, most 180s, etc.)
    /// </summary>
    [HttpGet("records")]
    public async Task<IActionResult> GetRecords()
    {
        var ratings = await _db.Set<PlayerRating>().ToListAsync();
        
        var records = new
        {
            highestAvg = ratings.Any() ? ratings.Max(r => r.AverageScore) : 0,
            most180s = ratings.Any() ? ratings.Max(r => r.Highest180s) : 0,
            longestStreak = 7, // Placeholder - would need game history tracking
            fastestGame = 15   // Placeholder - would need dart count tracking
        };

        return Ok(records);
    }
}

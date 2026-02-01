using Microsoft.AspNetCore.Mvc;
using DartGameAPI.Services;

namespace DartGameAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevController : ControllerBase
{
    private readonly MatchmakingService _matchmaking;
    private readonly ILogger<DevController> _logger;

    public DevController(MatchmakingService matchmaking, ILogger<DevController> logger)
    {
        _matchmaking = matchmaking;
        _logger = logger;
    }

    /// <summary>
    /// Seed fake online players for testing
    /// </summary>
    [HttpPost("seed-players")]
    public async Task<IActionResult> SeedFakePlayers([FromQuery] int count = 25)
    {
        var fakeLocations = new[]
        {
            ("New York", 40.7128, -74.0060),
            ("Los Angeles", 34.0522, -118.2437),
            ("Chicago", 41.8781, -87.6298),
            ("Houston", 29.7604, -95.3698),
            ("Phoenix", 33.4484, -112.0740),
            ("London", 51.5074, -0.1278),
            ("Paris", 48.8566, 2.3522),
            ("Berlin", 52.5200, 13.4050),
            ("Tokyo", 35.6762, 139.6503),
            ("Sydney", -33.8688, 151.2093),
            ("Toronto", 43.6532, -79.3832),
            ("Mexico City", 19.4326, -99.1332),
            ("São Paulo", -23.5505, -46.6333),
            ("Mumbai", 19.0760, 72.8777),
            ("Singapore", 1.3521, 103.8198),
            ("Dubai", 25.2048, 55.2708),
            ("Amsterdam", 52.3676, 4.9041),
            ("Seoul", 37.5665, 126.9780),
            ("Melbourne", -37.8136, 144.9631),
            ("Barcelona", 41.3851, 2.1734),
            ("Miami", 25.7617, -80.1918),
            ("Las Vegas", 36.1699, -115.1398),
            ("Denver", 39.7392, -104.9903),
            ("Seattle", 47.6062, -122.3321),
            ("Boston", 42.3601, -71.0589),
        };

        var names = new[]
        {
            "DartMaster180", "BullseyeKing", "TripleTwenty", "ArrowAce", "SteelTipSteve",
            "FlightPath", "Checkout_Charlie", "DoubleTrouble", "CricketKing", "Shanghai_Sam",
            "NineDarter", "MadHouse", "TonEighty", "WireWizard", "BoardBoss",
            "FinishFirst", "OutshotOllie", "SegmentSlayer", "DartDemon", "BrassMonkey",
            "TungstenTom", "CorkChamp", "OcheMaster", "LegLegend", "SetStar"
        };

        var random = new Random();
        var seeded = new List<object>();

        for (int i = 0; i < Math.Min(count, fakeLocations.Length); i++)
        {
            var loc = fakeLocations[i];
            var name = names[i % names.Length];
            var playerId = Guid.NewGuid();
            var connectionId = $"fake-{playerId:N}";

            // Add some randomness to coordinates
            var lat = loc.Item2 + (random.NextDouble() - 0.5) * 2;
            var lon = loc.Item3 + (random.NextDouble() - 0.5) * 2;

            await _matchmaking.PlayerConnected(playerId, connectionId, null, lat, lon);

            seeded.Add(new { playerId, name, location = loc.Item1, lat, lon });
        }

        _logger.LogInformation("Seeded {Count} fake online players", seeded.Count);

        return Ok(new { 
            message = $"Seeded {seeded.Count} fake players",
            players = seeded
        });
    }

    /// <summary>
    /// Clear all fake players
    /// </summary>
    [HttpDelete("clear-players")]
    public IActionResult ClearFakePlayers()
    {
        // This would need access to internal state - for now just return info
        return Ok(new { message = "Restart API to clear fake players" });
    }

    /// <summary>
    /// Get online status summary
    /// </summary>
    [HttpGet("online-summary")]
    public IActionResult GetOnlineSummary()
    {
        var players = _matchmaking.GetOnlinePlayers().ToList();
        var mapPoints = _matchmaking.GetPlayerMapPoints().ToList();

        return Ok(new
        {
            totalOnline = players.Count,
            withLocation = mapPoints.Count,
            byStatus = players.GroupBy(p => p.Status).Select(g => new { status = g.Key.ToString(), count = g.Count() }),
            players = mapPoints.Select(p => new { p.PlayerId, p.Latitude, p.Longitude, p.Status })
        });
    }
}

using DartGameAPI.Services;
using DartGameAPI.Hubs;
using DartGameAPI.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DartGame API", Version = "v1" });
});

builder.Services.AddMemoryCache();

// Database context
builder.Services.AddDbContext<DartsMobDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DartsMobDB")));

// SignalR for real-time updates
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// CORS - allow all for now (need credentials for SignalR)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)  // Allow any origin
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();  // Required for SignalR
    });
});

// DartDetect API client - forwards images for scoring
builder.Services.AddHttpClient<DartDetectClient>();

// NOTE: DartSensorClient removed - sensor communication now via SignalR
// Sensor connects to GameHub and receives StartGame/StopGame/Rebase events

// Game service (singleton - holds all state)
builder.Services.AddSingleton<GameService>();

// Matchmaking service (singleton - manages online play)
builder.Services.AddSingleton<MatchmakingService>();

// NOTE: DartSensorService removed in v2.0
// DartDetect now runs continuous motion detection and pushes dart detections to us
// See: POST /api/games/board/{boardId}/dart-detected

var app = builder.Build();

// Verify database connection on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DartsMobDbContext>();
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        if (canConnect)
        {
            app.Logger.LogInformation("✓ Connected to DartsMobDB");
        }
        else
        {
            app.Logger.LogError("✗ Cannot connect to DartsMobDB");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "✗ Database connection failed");
    }
}

// Configure pipeline
app.UseSwagger();
app.UseSwaggerUI();

// Serve static files from wwwroot
app.UseDefaultFiles();  // Serves index.html by default
app.UseStaticFiles();

app.UseCors();

app.MapControllers();

// Map SignalR hubs
app.MapHub<GameHub>("/gamehub");
app.MapHub<OnlineGameHub>("/onlinehub");

// Load all boards from DB on startup
var gameService = app.Services.GetRequiredService<GameService>();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DartGameAPI.Data.DartsMobDbContext>();
    var boards = db.Boards.Where(b => b.IsActive).ToList();
    var cameras = db.Cameras.Where(c => c.IsActive).ToList();
    foreach (var board in boards)
    {
        var boardCameras = cameras.Where(c => c.BoardId == board.BoardId).Select(c => c.CameraId).ToList();
        gameService.RegisterBoard(board.BoardId, board.Name, boardCameras);
        Console.WriteLine($"Registered board: {board.Name} ({board.BoardId}) with {boardCameras.Count} cameras");
    }
    if (!boards.Any())
    {
        // Fallback: register default board
        gameService.RegisterBoard("default", "Default Board", new List<string> { "cam0", "cam1", "cam2" });
        Console.WriteLine("No boards in DB, registered default board");
    }
}

// Health check
app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

// Root redirect to UI
app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

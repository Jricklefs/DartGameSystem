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

// Database context
builder.Services.AddDbContext<DartsMobDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DartsMobDB")));

// SignalR for real-time updates
builder.Services.AddSignalR();

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

// DartDetect API client
builder.Services.AddHttpClient<DartDetectClient>();

// Game service (singleton - holds all state)
builder.Services.AddSingleton<GameService>();

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

// Register default board on startup (can be configured)
var gameService = app.Services.GetRequiredService<GameService>();
gameService.RegisterBoard("default", "Default Board", new List<string> { "cam0", "cam1", "cam2" });

// Health check
app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

// Root redirect to UI
app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

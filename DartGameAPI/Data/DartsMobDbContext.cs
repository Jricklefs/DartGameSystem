using DartGameAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DartGameAPI.Data;

/// <summary>
/// Entity Framework DbContext for DartsMobDB
/// </summary>
public class DartsMobDbContext : DbContext
{
    public DartsMobDbContext(DbContextOptions<DartsMobDbContext> options) : base(options)
    {
    }

    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();
    public DbSet<BoardEntity> Boards => Set<BoardEntity>();
    public DbSet<CameraEntity> Cameras => Set<CameraEntity>();  // NEW: Camera tracking
    public DbSet<GameEntity> Games => Set<GameEntity>();
    public DbSet<GamePlayerEntity> GamePlayers => Set<GamePlayerEntity>();
    public DbSet<ThrowEntity> Throws => Set<ThrowEntity>();
    public DbSet<CalibrationEntity> Calibrations => Set<CalibrationEntity>();
    public DbSet<DartLocation> DartLocations => Set<DartLocation>();  // For heatmap data
    
    // Online play entities
    public DbSet<RegisteredBoard> RegisteredBoards => Set<RegisteredBoard>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<PlayerRating> PlayerRatings => Set<PlayerRating>();
    public DbSet<Availability> Availabilities => Set<Availability>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Players
        modelBuilder.Entity<PlayerEntity>(entity =>
        {
            entity.ToTable("Players");
            entity.HasKey(e => e.PlayerId);
            entity.Property(e => e.Nickname).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.AvatarUrl).HasMaxLength(500);
            entity.HasIndex(e => e.Nickname).IsUnique();
        });

        // Boards
        modelBuilder.Entity<BoardEntity>(entity =>
        {
            entity.ToTable("Boards");
            entity.HasKey(e => e.BoardId);
            entity.Property(e => e.BoardId).HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Location).HasMaxLength(255);
        });

        // Cameras (NEW)
        modelBuilder.Entity<CameraEntity>(entity =>
        {
            entity.ToTable("Cameras");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CameraId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.BoardId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(100);
            entity.Property(e => e.CalibrationQuality).HasColumnType("decimal(5,4)");
            entity.HasIndex(e => new { e.BoardId, e.CameraId }).IsUnique();
            entity.HasIndex(e => e.BoardId);
        });

        // Games
        modelBuilder.Entity<GameEntity>(entity =>
        {
            entity.ToTable("Games");
            entity.HasKey(e => e.GameId);
            entity.HasOne<BoardEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.BoardId);
            entity.HasOne<PlayerEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.WinnerPlayerId);
        });

        // GamePlayers
        modelBuilder.Entity<GamePlayerEntity>(entity =>
        {
            entity.ToTable("GamePlayers");
            entity.HasKey(e => e.GamePlayerId);
            entity.HasOne<GameEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.GameId);
            entity.HasOne<PlayerEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.PlayerId);
            entity.HasIndex(e => new { e.GameId, e.PlayerId }).IsUnique();
        });

        // Throws
        modelBuilder.Entity<ThrowEntity>(entity =>
        {
            entity.ToTable("Throws");
            entity.HasKey(e => e.ThrowId);
            entity.HasOne<GamePlayerEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.GamePlayerId);
            entity.Property(e => e.XMm).HasColumnType("decimal(8,2)");
            entity.Property(e => e.YMm).HasColumnType("decimal(8,2)");
            entity.Property(e => e.Confidence).HasColumnType("decimal(5,4)");
        });

        // Calibrations
        modelBuilder.Entity<CalibrationEntity>(entity =>
        {
            entity.ToTable("Calibrations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CameraId).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => e.CameraId).IsUnique();
        });

        // DartLocations (for heatmap data)
        modelBuilder.Entity<DartLocation>(entity =>
        {
            entity.ToTable("DartLocations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GameId).HasMaxLength(50);
            entity.Property(e => e.PlayerId).HasMaxLength(50);
            entity.Property(e => e.CameraId).HasMaxLength(50);
            entity.Property(e => e.XMm).HasColumnType("decimal(8,2)");
            entity.Property(e => e.YMm).HasColumnType("decimal(8,2)");
            entity.Property(e => e.Confidence).HasColumnType("decimal(5,4)");
            entity.HasIndex(e => e.PlayerId);
            entity.HasIndex(e => e.GameId);
            entity.HasIndex(e => e.DetectedAt);
        });

        // ============================================================================
        // Online Play Entities
        // ============================================================================

        // RegisteredBoards
        modelBuilder.Entity<RegisteredBoard>(entity =>
        {
            entity.ToTable("RegisteredBoards");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Location).HasMaxLength(100);
            entity.Property(e => e.Timezone).HasMaxLength(50);
            entity.HasOne(e => e.Owner)
                  .WithMany()
                  .HasForeignKey(e => e.OwnerId)
                  .HasPrincipalKey(p => p.PlayerId);
        });

        // Friendships
        modelBuilder.Entity<Friendship>(entity =>
        {
            entity.ToTable("Friendships");
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Requester)
                  .WithMany()
                  .HasForeignKey(e => e.RequesterId)
                  .HasPrincipalKey(p => p.PlayerId)
                  .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.Addressee)
                  .WithMany()
                  .HasForeignKey(e => e.AddresseeId)
                  .HasPrincipalKey(p => p.PlayerId)
                  .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(e => new { e.RequesterId, e.AddresseeId }).IsUnique();
        });

        // PlayerRatings
        modelBuilder.Entity<PlayerRating>(entity =>
        {
            entity.ToTable("PlayerRatings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GameMode).HasMaxLength(50).IsRequired();
            entity.HasOne(e => e.Player)
                  .WithMany()
                  .HasForeignKey(e => e.PlayerId)
                  .HasPrincipalKey(p => p.PlayerId);
            entity.HasIndex(e => new { e.PlayerId, e.GameMode }).IsUnique();
        });

        // Availability
        modelBuilder.Entity<Availability>(entity =>
        {
            entity.ToTable("Availability");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timezone).HasMaxLength(50);
            entity.HasOne(e => e.Player)
                  .WithMany()
                  .HasForeignKey(e => e.PlayerId)
                  .HasPrincipalKey(p => p.PlayerId);
        });
    }
}

// ============================================================================
// Entity Classes (match database tables)
// ============================================================================

public class PlayerEntity
{
    public Guid PlayerId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }
    
    // For navigation from online models
    public string Name => Nickname;
}

public class BoardEntity
{
    public string BoardId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public int CameraCount { get; set; }
    public bool IsCalibrated { get; set; }
    public DateTime? LastCalibration { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public string? CalibrationData { get; set; }  // JSON blob from DartDetect
}

// NEW: Camera entity for tracking individual cameras per board
public class CameraEntity
{
    public int Id { get; set; }
    public string CameraId { get; set; } = string.Empty;  // e.g. "cam0", "cam1"
    public string BoardId { get; set; } = string.Empty;   // FK to Boards
    public int DeviceIndex { get; set; }                  // USB device index
    public string? DisplayName { get; set; }              // User-friendly name
    public bool IsCalibrated { get; set; }
    public double? CalibrationQuality { get; set; }       // 0-1 quality score
    public DateTime? LastCalibration { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

public class GameEntity
{
    public Guid GameId { get; set; }
    public string BoardId { get; set; } = string.Empty;
    public int GameMode { get; set; }
    public int GameState { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Guid? WinnerPlayerId { get; set; }
    public int TotalDarts { get; set; }
    public int? DurationSeconds { get; set; }
}

public class GamePlayerEntity
{
    public Guid GamePlayerId { get; set; }
    public Guid GameId { get; set; }
    public Guid PlayerId { get; set; }
    public int PlayerOrder { get; set; }
    public int StartingScore { get; set; }
    public int? FinalScore { get; set; }
    public int DartsThrown { get; set; }
    public int? HighestTurn { get; set; }
    public bool IsWinner { get; set; }
}

public class ThrowEntity
{
    public Guid ThrowId { get; set; }
    public Guid GamePlayerId { get; set; }
    public int TurnNumber { get; set; }
    public int DartIndex { get; set; }
    public int Segment { get; set; }
    public int Multiplier { get; set; }
    public int Score { get; set; }
    public decimal? XMm { get; set; }
    public decimal? YMm { get; set; }
    public decimal? Confidence { get; set; }
    public bool IsBust { get; set; }
    public DateTime ThrownAt { get; set; }
}

public class CalibrationEntity
{
    public int Id { get; set; }
    public string CameraId { get; set; } = string.Empty;
    public string? CalibrationImagePath { get; set; }
    public string? OverlayImagePath { get; set; }
    public double Quality { get; set; }
    public double? TwentyAngle { get; set; }
    public string? CalibrationModel { get; set; }
    public string? CalibrationData { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

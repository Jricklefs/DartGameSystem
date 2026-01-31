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
    public DbSet<GameEntity> Games => Set<GameEntity>();
    public DbSet<GamePlayerEntity> GamePlayers => Set<GamePlayerEntity>();
    public DbSet<ThrowEntity> Throws => Set<ThrowEntity>();

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

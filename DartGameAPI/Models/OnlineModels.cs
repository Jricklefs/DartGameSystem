using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DartGameAPI.Data;

namespace DartGameAPI.Models;

/// <summary>
/// A registered board in the DartsMob network
/// </summary>
public class RegisteredBoard
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    
    [Required]
    public Guid OwnerId { get; set; }  // Player who owns this board
    
    [MaxLength(100)]
    public string? Location { get; set; }  // "Chicago, IL" or custom
    
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    
    [MaxLength(50)]
    public string? Timezone { get; set; }
    
    public bool IsPublic { get; set; } = true;  // Show on map/matchmaking
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastOnlineAt { get; set; }
    
    // Navigation
    public PlayerEntity? Owner { get; set; }
}

/// <summary>
/// Friend relationship between players
/// </summary>
public class Friendship
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [Required]
    public Guid RequesterId { get; set; }
    
    [Required]
    public Guid AddresseeId { get; set; }
    
    public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }
    
    // Navigation
    public PlayerEntity? Requester { get; set; }
    public PlayerEntity? Addressee { get; set; }
}

public enum FriendshipStatus
{
    Pending,
    Accepted,
    Declined,
    Blocked
}

/// <summary>
/// Player skill rating (Elo-based)
/// </summary>
public class PlayerRating
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [Required]
    public Guid PlayerId { get; set; }
    
    [Required, MaxLength(50)]
    public string GameMode { get; set; } = "Game501";  // Rating per game type
    
    public int Rating { get; set; } = 1200;  // Starting Elo
    public int GamesPlayed { get; set; } = 0;
    public int Wins { get; set; } = 0;
    public int Losses { get; set; } = 0;
    
    public double AverageScore { get; set; } = 0;  // Per turn average
    public double CheckoutPercentage { get; set; } = 0;
    public int Highest180s { get; set; } = 0;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation
    public PlayerEntity? Player { get; set; }
}

/// <summary>
/// Player availability schedule
/// </summary>
public class Availability
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [Required]
    public Guid PlayerId { get; set; }
    
    public DayOfWeek? DayOfWeek { get; set; }  // null = specific date
    public DateTime? SpecificDate { get; set; }  // For one-time availability
    
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    
    [MaxLength(50)]
    public string? Timezone { get; set; }
    
    public bool IsRecurring { get; set; } = true;
    
    // Navigation
    public PlayerEntity? Player { get; set; }
}

/// <summary>
/// Active matchmaking queue entry
/// </summary>
public class MatchmakingEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [Required]
    public Guid PlayerId { get; set; }
    
    [Required]
    public Guid BoardId { get; set; }
    
    [Required, MaxLength(50)]
    public string GameMode { get; set; } = "Game501";
    
    public MatchmakingPreference Preference { get; set; } = MatchmakingPreference.Anyone;
    
    public int? TargetRatingMin { get; set; }
    public int? TargetRatingMax { get; set; }
    
    [MaxLength(100)]
    public string? PreferredRegion { get; set; }  // "US", "EU", etc.
    
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    
    [MaxLength(100)]
    public string? ConnectionId { get; set; }  // SignalR connection
    
    // Navigation
    public PlayerEntity? Player { get; set; }
    public RegisteredBoard? Board { get; set; }
}

public enum MatchmakingPreference
{
    Anyone,           // Match with anyone
    SimilarSkill,     // Within ~200 rating points
    ChosenSkill,      // User-selected range
    FriendsOnly       // Only match with friends
}

/// <summary>
/// Online player session (in-memory, not persisted)
/// </summary>
public class OnlineSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlayerId { get; set; }
    public Guid? BoardId { get; set; }
    public string ConnectionId { get; set; } = "";
    public PlayerStatus Status { get; set; } = PlayerStatus.Online;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
}

public enum PlayerStatus
{
    Offline,
    Online,
    InQueue,
    InMatch,
    Away
}

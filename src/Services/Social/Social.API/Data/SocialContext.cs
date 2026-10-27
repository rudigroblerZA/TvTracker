using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Social.API.Data;

/// <summary>A user as others see them here: a name from Identity, and whether they share what they watch.</summary>
public class Profile
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Whether the people who can see this user see their activity. On unless they turn it off.</summary>
    public bool ShareActivity { get; set; } = true;

    /// <summary>Followers need approving: until then a request is pending, and only approved followers see anything.</summary>
    public bool Private { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One user following another. One way, and following back is a separate choice. Following a private profile is a
/// request until the followee approves it.
/// </summary>
public class UserFollow
{
    public required string FollowerId { get; set; }
    public required string FolloweeId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>False while it's a request to a private profile.</summary>
    public bool Approved { get; set; } = true;
}

/// <summary>A block. Either way round, the two don't see each other: not in search, pages, lists or anywhere else.</summary>
public class UserBlock
{
    public required string BlockerId { get; set; }
    public required string BlockedId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A user's relation to a show, from Tracking's and Ratings' events and snapshots: followed, rated.</summary>
public class UserShow
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public bool Followed { get; set; }
    public DateTime? FollowedAtUtc { get; set; }

    /// <summary>Their show rating, 1-5.</summary>
    public int? Stars { get; set; }
}

/// <summary>A watched episode, from Tracking's events and snapshots: where people are up to in a show.</summary>
public class UserWatch
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime WatchedAtUtc { get; set; }
}

/// <summary>A list its owner shares, copied from Tracking. Who can see it follows Social's rules (blocks, private profiles).</summary>
public class SharedList
{
    public Guid ListId { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<Guid> ShowIds { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One thing a user did, as their followers see it in the feed. Episodes watched of one show close together are one item
/// ("watched 3 episodes"), so a binge doesn't fill the feed.
/// </summary>
public class Activity
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }

    /// <summary>One of <see cref="ActivityKinds"/>.</summary>
    public required string Kind { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>For "watched": the episodes, in the order they were marked.</summary>
    public List<Guid> EpisodeIds { get; set; } = [];

    /// <summary>For "rated": the stars, 1-5.</summary>
    public int? Stars { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}

public static class ActivityKinds
{
    public const string Watched = "watched";
    public const string Finished = "finished";
    public const string Followed = "followed";
    public const string Rated = "rated";
}

/// <summary>The catalog's show title and poster, for rendering the feed.</summary>
public class ShowCopy
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
}

/// <summary>An episode's numbers, for "S02E03" in the feed. Titles aren't kept: a follower may not have seen it yet.</summary>
public class EpisodeCopy
{
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }

    /// <summary>For counting what's aired when comparing progress.</summary>
    public DateTimeOffset? AirsAtUtc { get; set; }
}

public class SocialContext(DbContextOptions<SocialContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<UserFollow> Follows => Set<UserFollow>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ShowCopy> Shows => Set<ShowCopy>();
    public DbSet<EpisodeCopy> Episodes => Set<EpisodeCopy>();
    public DbSet<UserBlock> Blocks => Set<UserBlock>();
    public DbSet<UserShow> UserShows => Set<UserShow>();
    public DbSet<UserWatch> Watches => Set<UserWatch>();
    public DbSet<SharedList> SharedLists => Set<SharedList>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Profile>(entity =>
        {
            entity.ToTable("profiles");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.DisplayName).HasMaxLength(128);
            entity.HasIndex(p => p.DisplayName);
        });

        modelBuilder.Entity<UserFollow>(entity =>
        {
            entity.ToTable("follows");
            entity.HasKey(f => new { f.FollowerId, f.FolloweeId });
            entity.HasIndex(f => f.FolloweeId);
            entity.Property(f => f.FollowerId).HasMaxLength(64);
            entity.Property(f => f.FolloweeId).HasMaxLength(64);
            entity.Property(f => f.Approved).HasDefaultValue(true);
        });

        modelBuilder.Entity<UserBlock>(entity =>
        {
            entity.ToTable("blocks");
            entity.HasKey(b => new { b.BlockerId, b.BlockedId });
            entity.HasIndex(b => b.BlockedId);
            entity.Property(b => b.BlockerId).HasMaxLength(64);
            entity.Property(b => b.BlockedId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserShow>(entity =>
        {
            entity.ToTable("user_shows");
            entity.HasKey(s => new { s.UserId, s.ShowId });
            entity.HasIndex(s => s.ShowId);
            entity.Property(s => s.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserWatch>(entity =>
        {
            entity.ToTable("user_watches");
            entity.HasKey(w => new { w.UserId, w.EpisodeId });
            entity.HasIndex(w => new { w.ShowId, w.UserId });
            entity.Property(w => w.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<SharedList>(entity =>
        {
            entity.ToTable("shared_lists");
            entity.HasKey(l => l.ListId);
            entity.Property(l => l.ListId).ValueGeneratedNever();
            entity.HasIndex(l => l.UserId);
            entity.Property(l => l.UserId).HasMaxLength(64);
            entity.Property(l => l.Name).HasMaxLength(100);
            entity.Property(l => l.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<Activity>(entity =>
        {
            entity.ToTable("activities");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.UserId).HasMaxLength(64);
            entity.Property(a => a.Kind).HasMaxLength(16);
            entity.HasIndex(a => new { a.UserId, a.OccurredAtUtc });
        });

        modelBuilder.Entity<ShowCopy>(entity =>
        {
            entity.ToTable("shows");
            entity.HasKey(s => s.ShowId);
            entity.Property(s => s.ShowId).ValueGeneratedNever();
            entity.Property(s => s.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<EpisodeCopy>(entity =>
        {
            entity.ToTable("episodes");
            entity.HasKey(e => e.EpisodeId);
            entity.Property(e => e.EpisodeId).ValueGeneratedNever();
            entity.HasIndex(e => e.ShowId);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

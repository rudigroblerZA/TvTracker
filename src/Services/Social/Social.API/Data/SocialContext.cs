using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Social.API.Data;

/// <summary>A user as others see them here: a name from Identity, and whether they share what they watch.</summary>
public class Profile
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Whether the people who follow this user see their activity. On unless they turn it off.</summary>
    public bool ShareActivity { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One user following another. One way: no approval, and following back is a separate choice.</summary>
public class UserFollow
{
    public required string FollowerId { get; set; }
    public required string FolloweeId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
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
}

public class SocialContext(DbContextOptions<SocialContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<UserFollow> Follows => Set<UserFollow>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ShowCopy> Shows => Set<ShowCopy>();
    public DbSet<EpisodeCopy> Episodes => Set<EpisodeCopy>();

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

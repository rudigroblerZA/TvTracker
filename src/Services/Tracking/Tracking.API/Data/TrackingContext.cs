using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Tracking.API.Data;

public enum WatchStatus
{
    Watching,
    PlanToWatch,
    Completed,
    Paused,
    Stopped
}

public class Follow
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public WatchStatus Status { get; set; }
    public DateTime FollowedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class WatchedEpisode
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime WatchedAtUtc { get; set; }
}

public class UserList
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<UserListItem> Items { get; set; } = [];
}

public class UserListItem
{
    public Guid ListId { get; set; }
    public Guid ShowId { get; set; }
    public int Position { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>Read copy of a show, kept current from Catalog events.</summary>
public class ShowCopy
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Read copy of an episode, kept current from EpisodeScheduled events.</summary>
public class EpisodeCopy
{
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset? AirsAtUtc { get; set; }
    public int? RuntimeMinutes { get; set; }
}

public class TrackingContext(DbContextOptions<TrackingContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<WatchedEpisode> WatchedEpisodes => Set<WatchedEpisode>();
    public DbSet<UserList> Lists => Set<UserList>();
    public DbSet<UserListItem> ListItems => Set<UserListItem>();
    public DbSet<ShowCopy> Shows => Set<ShowCopy>();
    public DbSet<EpisodeCopy> Episodes => Set<EpisodeCopy>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Follow>(entity =>
        {
            entity.ToTable("follows");
            entity.HasKey(f => new { f.UserId, f.ShowId });
            entity.HasIndex(f => f.ShowId);
            entity.Property(f => f.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<WatchedEpisode>(entity =>
        {
            entity.ToTable("watched_episodes");
            entity.HasKey(w => new { w.UserId, w.EpisodeId });
            entity.HasIndex(w => new { w.UserId, w.ShowId });
            entity.Property(w => w.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserList>(entity =>
        {
            entity.ToTable("lists");
            entity.HasIndex(l => l.UserId);
            entity.Property(l => l.UserId).HasMaxLength(64);
            entity.Property(l => l.Name).HasMaxLength(100);
            entity.Property(l => l.Description).HasMaxLength(1000);
            entity.HasMany(l => l.Items).WithOne().HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserListItem>(entity =>
        {
            entity.ToTable("list_items");
            entity.HasKey(i => new { i.ListId, i.ShowId });
        });

        modelBuilder.Entity<ShowCopy>(entity =>
        {
            entity.ToTable("shows");
            entity.HasKey(s => s.ShowId);
            entity.Property(s => s.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<EpisodeCopy>(entity =>
        {
            entity.ToTable("episodes");
            entity.HasKey(e => e.EpisodeId);
            entity.HasIndex(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber });
            entity.HasIndex(e => e.AirsAtUtc);
            entity.Property(e => e.Title).HasMaxLength(256);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Ratings.API.Data;

public class ShowRating
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public int Stars { get; set; }
    public DateTime RatedAtUtc { get; set; }
}

public class EpisodeRating
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int Stars { get; set; }
    public DateTime RatedAtUtc { get; set; }
}

public class Comment
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public required string Text { get; set; }

    /// <summary>The author says the comment contains spoilers; it stays hidden until the reader opts in.</summary>
    public bool IsSpoiler { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public class Reaction
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public required string Emoji { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Local copy of watched episodes (from EpisodeWatched), used to decide whether to show comments.</summary>
public class WatchedEpisodeCopy
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
}

public class RatingsContext(DbContextOptions<RatingsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<ShowRating> ShowRatings => Set<ShowRating>();
    public DbSet<EpisodeRating> EpisodeRatings => Set<EpisodeRating>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<WatchedEpisodeCopy> WatchedEpisodes => Set<WatchedEpisodeCopy>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShowRating>(entity =>
        {
            entity.ToTable("show_ratings");
            entity.HasKey(r => new { r.UserId, r.ShowId });
            entity.HasIndex(r => r.ShowId);
            entity.Property(r => r.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<EpisodeRating>(entity =>
        {
            entity.ToTable("episode_ratings");
            entity.HasKey(r => new { r.UserId, r.EpisodeId });
            entity.HasIndex(r => r.EpisodeId);
            entity.Property(r => r.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<Comment>(entity =>
        {
            entity.ToTable("comments");
            entity.HasIndex(c => new { c.EpisodeId, c.CreatedAtUtc });
            entity.HasIndex(c => c.UserId);
            entity.Property(c => c.UserId).HasMaxLength(64);
            entity.Property(c => c.DisplayName).HasMaxLength(100);
            entity.Property(c => c.Text).HasMaxLength(2000);
        });

        modelBuilder.Entity<Reaction>(entity =>
        {
            entity.ToTable("reactions");
            entity.HasKey(r => new { r.UserId, r.EpisodeId });
            entity.HasIndex(r => r.EpisodeId);
            entity.Property(r => r.UserId).HasMaxLength(64);
            entity.Property(r => r.Emoji).HasMaxLength(16);
        });

        modelBuilder.Entity<WatchedEpisodeCopy>(entity =>
        {
            entity.ToTable("watched_episodes");
            entity.HasKey(w => new { w.UserId, w.EpisodeId });
            entity.Property(w => w.UserId).HasMaxLength(64);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

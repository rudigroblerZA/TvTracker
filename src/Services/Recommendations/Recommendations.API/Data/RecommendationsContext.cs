using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Recommendations.API.Data;

/// <summary>What Recommendations knows about a show, from ShowAdded/ShowUpdated.</summary>
public class ShowFeatures
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    public List<string> CastNames { get; set; } = [];

    /// <summary>The catalog provider's "similar shows", most similar first.</summary>
    public List<Guid> SimilarShowIds { get; set; } = [];

    public double Popularity { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One user's relationship with one show: follow, episodes watched, rating.</summary>
public class UserShowSignal
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public bool Followed { get; set; }
    public int EpisodesWatched { get; set; }
    public int? Rating { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class Dismissal
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime DismissedAtUtc { get; set; }
}

/// <summary>Pre-computed "for you" list, rebuilt in the background when the user's signals change.</summary>
public class UserRecommendation
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public int Rank { get; set; }
    public double Score { get; set; }
    public required string Reason { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>Users whose recommendations need rebuilding.</summary>
public class DirtyUser
{
    public required string UserId { get; set; }
    public DateTime MarkedAtUtc { get; set; }
}

public class RecommendationsContext(DbContextOptions<RecommendationsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<ShowFeatures> Shows => Set<ShowFeatures>();
    public DbSet<UserShowSignal> Signals => Set<UserShowSignal>();
    public DbSet<Dismissal> Dismissals => Set<Dismissal>();
    public DbSet<UserRecommendation> UserRecommendations => Set<UserRecommendation>();
    public DbSet<DirtyUser> DirtyUsers => Set<DirtyUser>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShowFeatures>(entity =>
        {
            entity.ToTable("shows");
            entity.HasKey(s => s.ShowId);
            entity.Property(s => s.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<UserShowSignal>(entity =>
        {
            entity.ToTable("user_show_signals");
            entity.HasKey(s => new { s.UserId, s.ShowId });
            entity.HasIndex(s => new { s.ShowId, s.Followed });
            entity.Property(s => s.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<Dismissal>(entity =>
        {
            entity.ToTable("dismissals");
            entity.HasKey(d => new { d.UserId, d.ShowId });
            entity.Property(d => d.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserRecommendation>(entity =>
        {
            entity.ToTable("user_recommendations");
            entity.HasKey(r => new { r.UserId, r.ShowId });
            entity.HasIndex(r => new { r.UserId, r.Rank });
            entity.Property(r => r.UserId).HasMaxLength(64);
            entity.Property(r => r.Reason).HasMaxLength(256);
        });

        modelBuilder.Entity<DirtyUser>(entity =>
        {
            entity.ToTable("dirty_users");
            entity.HasKey(d => d.UserId);
            entity.Property(d => d.UserId).HasMaxLength(64);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

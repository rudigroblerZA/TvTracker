using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Analytics.Data;

public class AnalyticsContext(DbContextOptions<AnalyticsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<ShowCopy> Shows => Set<ShowCopy>();
    public DbSet<EpisodeCopy> Episodes => Set<EpisodeCopy>();
    public DbSet<AnnouncedSeason> AnnouncedSeasons => Set<AnnouncedSeason>();
    public DbSet<CastCopy> Cast => Set<CastCopy>();
    public DbSet<UserShowRating> ShowRatings => Set<UserShowRating>();
    public DbSet<UserEpisodeRating> EpisodeRatings => Set<UserEpisodeRating>();
    public DbSet<UserEpisodeWatch> Watches => Set<UserEpisodeWatch>();
    public DbSet<UserFollow> Follows => Set<UserFollow>();

    public DbSet<ShowInsightsRow> ShowInsights => Set<ShowInsightsRow>();
    public DbSet<PremiereRow> Premieres => Set<PremiereRow>();
    public DbSet<TopEpisodeRow> TopEpisodes => Set<TopEpisodeRow>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.HasIndex(e => e.ShowId);
            entity.Property(e => e.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<AnnouncedSeason>(entity =>
        {
            entity.ToTable("announced_seasons");
            entity.HasKey(s => new { s.ShowId, s.SeasonNumber });
        });

        modelBuilder.Entity<CastCopy>(entity =>
        {
            entity.ToTable("cast_credits");
            entity.HasKey(c => new { c.ShowId, c.PersonName });
            entity.HasIndex(c => c.PersonName);
            entity.Property(c => c.PersonName).HasMaxLength(256);
            entity.Property(c => c.Character).HasMaxLength(256);
        });

        modelBuilder.Entity<UserShowRating>(entity =>
        {
            entity.ToTable("user_show_ratings");
            entity.HasKey(r => new { r.UserId, r.ShowId });
            entity.HasIndex(r => r.ShowId);
            entity.Property(r => r.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserEpisodeRating>(entity =>
        {
            entity.ToTable("user_episode_ratings");
            entity.HasKey(r => new { r.UserId, r.EpisodeId });
            entity.HasIndex(r => r.ShowId);
            entity.Property(r => r.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserEpisodeWatch>(entity =>
        {
            entity.ToTable("user_episode_watches");
            entity.HasKey(w => new { w.UserId, w.EpisodeId });
            entity.HasIndex(w => w.ShowId);
            entity.Property(w => w.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<UserFollow>(entity =>
        {
            entity.ToTable("user_follows");
            entity.HasKey(f => new { f.UserId, f.ShowId });
            entity.HasIndex(f => f.ShowId);
            entity.Property(f => f.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<ShowInsightsRow>(entity =>
        {
            entity.ToTable("show_insights");
            entity.HasKey(i => i.ShowId);
            entity.Property(i => i.Document).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PremiereRow>(entity =>
        {
            entity.ToTable("premieres");
            entity.HasKey(p => new { p.ShowId, p.SeasonNumber });
            entity.HasIndex(p => p.PremieresAtUtc);
            entity.Property(p => p.ShowTitle).HasMaxLength(256);
        });

        modelBuilder.Entity<TopEpisodeRow>(entity =>
        {
            entity.ToTable("top_episodes");
            entity.HasKey(t => new { t.Genre, t.Rank });
            entity.Property(t => t.Genre).HasMaxLength(64);
            entity.Property(t => t.ShowTitle).HasMaxLength(256);
            entity.Property(t => t.Title).HasMaxLength(256);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

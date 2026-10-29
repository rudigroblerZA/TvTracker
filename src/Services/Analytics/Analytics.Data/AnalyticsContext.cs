using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Analytics.Data;

public class AnalyticsContext(DbContextOptions<AnalyticsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<ShowCopy> Shows => Set<ShowCopy>();
    public DbSet<EpisodeCopy> Episodes => Set<EpisodeCopy>();
    public DbSet<AnnouncedSeason> AnnouncedSeasons => Set<AnnouncedSeason>();
    public DbSet<EpisodeNoteCopy> EpisodeNotes => Set<EpisodeNoteCopy>();
    public DbSet<CastCopy> Cast => Set<CastCopy>();
    public DbSet<CreativeCopy> Creatives => Set<CreativeCopy>();
    public DbSet<PersonCareerCopy> Careers => Set<PersonCareerCopy>();
    public DbSet<PersonCastCreditCopy> CastCredits => Set<PersonCastCreditCopy>();
    public DbSet<UserShowRating> ShowRatings => Set<UserShowRating>();
    public DbSet<UserEpisodeRating> EpisodeRatings => Set<UserEpisodeRating>();
    public DbSet<UserEpisodeWatch> Watches => Set<UserEpisodeWatch>();
    public DbSet<UserFollow> Follows => Set<UserFollow>();
    public DbSet<UserPlanCopy> Plans => Set<UserPlanCopy>();
    public DbSet<UserService> UserServices => Set<UserService>();
    public DbSet<SeasonCall> SeasonCalls => Set<SeasonCall>();

    public DbSet<ShowInsightsRow> ShowInsights => Set<ShowInsightsRow>();
    public DbSet<PremiereRow> Premieres => Set<PremiereRow>();
    public DbSet<TopEpisodeRow> TopEpisodes => Set<TopEpisodeRow>();
    public DbSet<DiscoveryRow> Discovery => Set<DiscoveryRow>();
    public DbSet<PersonInsightsRow> PersonInsights => Set<PersonInsightsRow>();
    public DbSet<CreatorFingerprintRow> CreatorFingerprints => Set<CreatorFingerprintRow>();
    public DbSet<NetworkReportRow> NetworkReports => Set<NetworkReportRow>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShowCopy>(entity =>
        {
            entity.ToTable("shows");
            entity.HasKey(s => s.ShowId);
            entity.Property(s => s.Title).HasMaxLength(256);
            entity.Property(s => s.Network).HasMaxLength(128);
        });

        modelBuilder.Entity<EpisodeCopy>(entity =>
        {
            entity.ToTable("episodes");
            entity.HasKey(e => e.EpisodeId);
            entity.HasIndex(e => e.ShowId);
            // The time machine's weeks are ranges of air time.
            entity.HasIndex(e => e.AirsAtUtc);
            entity.Property(e => e.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<EpisodeNoteCopy>(entity =>
        {
            entity.ToTable("episode_notes");
            entity.HasKey(n => n.EpisodeId);
            entity.Property(n => n.EpisodeId).ValueGeneratedNever();
            entity.Property(n => n.Mark).HasMaxLength(16);
            entity.Property(n => n.Reason).HasMaxLength(280);
            entity.Property(n => n.SourceUrl).HasMaxLength(2048);
            entity.HasIndex(n => n.ShowId);
        });

        modelBuilder.Entity<AnnouncedSeason>(entity =>
        {
            entity.ToTable("announced_seasons");
            entity.HasKey(s => new { s.ShowId, s.SeasonNumber });
        });

        modelBuilder.Entity<CreativeCopy>(entity =>
        {
            entity.ToTable("creatives");
            entity.HasKey(c => new { c.ShowId, c.PersonId });
            entity.Property(c => c.Name).HasMaxLength(256);
        });

        modelBuilder.Entity<PersonCastCreditCopy>(entity =>
        {
            entity.ToTable("person_cast_credits");
            entity.HasKey(c => new { c.PersonId, c.ShowExternalId });
            entity.Property(c => c.ShowExternalId).HasMaxLength(64);
            entity.Property(c => c.Title).HasMaxLength(256);
            entity.Property(c => c.Character).HasMaxLength(512);
        });

        modelBuilder.Entity<PersonCareerCopy>(entity =>
        {
            entity.ToTable("careers");
            entity.HasKey(c => c.PersonId);
            entity.Property(c => c.Name).HasMaxLength(256);
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

        modelBuilder.Entity<DiscoveryRow>(entity =>
        {
            entity.ToTable("discovery");
            entity.HasKey(d => new { d.List, d.Genre, d.Rank });
            entity.Property(d => d.List).HasMaxLength(32);
            entity.Property(d => d.Genre).HasMaxLength(64);
            entity.Property(d => d.ShowTitle).HasMaxLength(256);
            entity.Property(d => d.EpisodeTitle).HasMaxLength(256);
        });

        modelBuilder.Entity<CreatorFingerprintRow>(entity =>
        {
            entity.ToTable("creator_fingerprints");
            entity.HasKey(p => p.PersonId);
            entity.Property(p => p.Document).HasColumnType("jsonb");
        });

        modelBuilder.Entity<UserPlanCopy>(entity =>
        {
            entity.ToTable("user_plans");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.Plan).HasMaxLength(16);
        });

        modelBuilder.Entity<UserService>(entity =>
        {
            entity.ToTable("user_services");
            entity.HasKey(s => new { s.UserId, s.ServiceKey });
            entity.Property(s => s.UserId).HasMaxLength(64);
            entity.Property(s => s.ServiceKey).HasMaxLength(32);
        });

        modelBuilder.Entity<SeasonCall>(entity =>
        {
            entity.ToTable("season_calls");
            entity.HasKey(c => new { c.UserId, c.ShowId, c.SeasonNumber });
            entity.Property(c => c.UserId).HasMaxLength(64);
            // The crowd's call per season, and the scorer's unscored calls.
            entity.HasIndex(c => new { c.ShowId, c.SeasonNumber });
            entity.HasIndex(c => c.ScoredAtUtc).HasFilter("\"ScoredAtUtc\" IS NULL");
        });

        modelBuilder.Entity<NetworkReportRow>(entity =>
        {
            entity.ToTable("network_reports");
            entity.HasKey(n => n.Network);
            entity.Property(n => n.Network).HasMaxLength(128);
            entity.Property(n => n.Document).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PersonInsightsRow>(entity =>
        {
            entity.ToTable("person_insights");
            entity.HasKey(p => p.PersonId);
            entity.Property(p => p.Document).HasColumnType("jsonb");
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

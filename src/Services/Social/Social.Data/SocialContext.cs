using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Social.Data;

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

    public DbSet<UserPlanCopy> Plans => Set<UserPlanCopy>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserPlanCopy>(entity =>
        {
            entity.ToTable("user_plans");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.Plan).HasMaxLength(16);
        });

        modelBuilder.Entity<Profile>(entity =>
        {
            entity.ToTable("profiles");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.DisplayName).HasMaxLength(128);
            entity.Property(p => p.Avatar).HasMaxLength(1024);
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

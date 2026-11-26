using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TvTracker.Persistence;

namespace Recommendations.Data;

public class RecommendationsContext(DbContextOptions<RecommendationsContext> options) : DbContext(options), IIntegrationEventLogDbContext, IUserPlanCopies
{
    public DbSet<ShowFeatures> Shows => Set<ShowFeatures>();
    public DbSet<ShowDna> Dna => Set<ShowDna>();
    public DbSet<ShowNanogenres> Nanogenres => Set<ShowNanogenres>();
    public DbSet<UserShowSignal> Signals => Set<UserShowSignal>();
    public DbSet<Dismissal> Dismissals => Set<Dismissal>();
    public DbSet<UserRecommendation> UserRecommendations => Set<UserRecommendation>();
    public DbSet<DirtyUser> DirtyUsers => Set<DirtyUser>();

    public DbSet<UserPlanCopy> Plans => Set<UserPlanCopy>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddUserPlanCopies();

        modelBuilder.Entity<ShowFeatures>(entity =>
        {
            entity.ToTable("shows");
            entity.HasKey(s => s.ShowId);
            entity.Property(s => s.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<ShowDna>(entity =>
        {
            entity.ToTable("show_dna");
            entity.HasKey(d => d.ShowId);
            entity.Property(d => d.ShowId).ValueGeneratedNever();
            entity.Property(d => d.Traits)
                .HasColumnType("jsonb")
                .HasConversion(
                    traits => JsonSerializer.Serialize(traits, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<Dictionary<string, double>>(json, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<Dictionary<string, double>>(
                        (a, b) => a!.Count == b!.Count && a.All(t => b.ContainsKey(t.Key) && b[t.Key] == t.Value),
                        traits => traits.Aggregate(0, (hash, t) => HashCode.Combine(hash, t.Key, t.Value)),
                        traits => new Dictionary<string, double>(traits)));
        });

        modelBuilder.Entity<ShowNanogenres>(entity =>
        {
            entity.ToTable("show_nanogenres");
            entity.HasKey(n => n.ShowId);
            entity.Property(n => n.ShowId).ValueGeneratedNever();
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

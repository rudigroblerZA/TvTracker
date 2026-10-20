using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Catalog.Data;

public class CatalogContext(DbContextOptions<CatalogContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<CastMember> Cast => Set<CastMember>();
    public DbSet<WatchProvider> WatchProviders => Set<WatchProvider>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Show>(entity =>
        {
            entity.ToTable("shows");
            entity.HasIndex(s => new { s.Source, s.ExternalId }).IsUnique();
            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.Popularity);
            entity.HasIndex(s => s.NextRefreshAtUtc);
            entity.Property(s => s.Source).HasMaxLength(32);
            entity.Property(s => s.ExternalId).HasMaxLength(64);
            entity.Property(s => s.Title).HasMaxLength(256);
            entity.Property(s => s.Network).HasMaxLength(128);

            entity.HasMany(s => s.Seasons).WithOne().HasForeignKey(s => s.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.Episodes).WithOne().HasForeignKey(e => e.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.Cast).WithOne().HasForeignKey(c => c.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.WatchProviders).WithOne().HasForeignKey(w => w.ShowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Season>(entity =>
        {
            entity.ToTable("seasons");
            entity.HasIndex(s => new { s.ShowId, s.Number }).IsUnique();
            entity.Property(s => s.Name).HasMaxLength(256);
        });

        modelBuilder.Entity<Episode>(entity =>
        {
            entity.ToTable("episodes");
            entity.HasIndex(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber });
            entity.HasIndex(e => new { e.ShowId, e.ExternalId }).IsUnique();
            entity.HasIndex(e => new { e.AiredPublished, e.AirsAtUtc });
            entity.Property(e => e.ExternalId).HasMaxLength(64);
            entity.Property(e => e.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<Person>(entity =>
        {
            entity.ToTable("people");
            entity.HasIndex(p => p.Name).IsUnique();
            entity.Property(p => p.Name).HasMaxLength(256);
        });

        modelBuilder.Entity<CastMember>(entity =>
        {
            entity.ToTable("cast_members");
            entity.HasKey(c => new { c.ShowId, c.PersonId });
            entity.HasOne(c => c.Person).WithMany().HasForeignKey(c => c.PersonId);
            entity.Property(c => c.Character).HasMaxLength(256);
        });

        modelBuilder.Entity<WatchProvider>(entity =>
        {
            entity.ToTable("watch_providers");
            entity.HasIndex(w => new { w.ShowId, w.Region });
            entity.Property(w => w.Region).HasMaxLength(2);
            entity.Property(w => w.ProviderName).HasMaxLength(128);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

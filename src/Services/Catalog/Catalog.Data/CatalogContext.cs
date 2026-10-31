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
    public DbSet<ShowCreative> Creatives => Set<ShowCreative>();
    public DbSet<PersonCastCredit> CastCredits => Set<PersonCastCredit>();
    public DbSet<Nanogenre> Nanogenres => Set<Nanogenre>();
    public DbSet<CatalogOverride> Overrides => Set<CatalogOverride>();
    public DbSet<WatchProvider> WatchProviders => Set<WatchProvider>();
    public DbSet<ShowEmbedding> ShowEmbeddings => Set<ShowEmbedding>();
    public DbSet<ShowQuoteSource> QuoteSources => Set<ShowQuoteSource>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<ShowSetting> Settings => Set<ShowSetting>();
    public DbSet<ShowSettingLookup> SettingLookups => Set<ShowSettingLookup>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Ids are set by the app (Guid.CreateVersion7) before saving. Without ValueGeneratedNever, EF takes an entity
        // with a set id that it finds in a navigation (a new episode added to show.Episodes on refresh) for an existing
        // row, and the UPDATE it sends affects nothing.
        modelBuilder.Entity<Show>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.ToTable("shows");
            entity.HasIndex(s => new { s.Source, s.ExternalId }).IsUnique();
            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.Popularity);
            entity.HasIndex(s => s.NextRefreshAtUtc);
            entity.Property(s => s.Source).HasMaxLength(32);
            entity.Property(s => s.ExternalId).HasMaxLength(64);
            entity.Property(s => s.Title).HasMaxLength(256);
            entity.Property(s => s.Network).HasMaxLength(128);
            entity.Property(s => s.Tags).HasDefaultValueSql("'{}'");
            entity.HasIndex(s => s.Tags).HasMethod("gin");
            entity.Property(s => s.Nanogenres).HasDefaultValueSql("'{}'");
            entity.HasIndex(s => s.Nanogenres).HasMethod("gin");

            entity.HasMany(s => s.Seasons).WithOne().HasForeignKey(s => s.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.Episodes).WithOne().HasForeignKey(e => e.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.Cast).WithOne().HasForeignKey(c => c.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.Creatives).WithOne().HasForeignKey(c => c.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.WatchProviders).WithOne().HasForeignKey(w => w.ShowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Season>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.ToTable("seasons");
            entity.HasIndex(s => new { s.ShowId, s.Number }).IsUnique();
            entity.Property(s => s.Name).HasMaxLength(256);
        });

        modelBuilder.Entity<Episode>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.ToTable("episodes");
            entity.HasIndex(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber });
            entity.HasIndex(e => new { e.ShowId, e.ExternalId }).IsUnique();
            entity.HasIndex(e => new { e.AiredPublished, e.AirsAtUtc });
            entity.Property(e => e.ExternalId).HasMaxLength(64);
            entity.Property(e => e.Title).HasMaxLength(256);
        });

        modelBuilder.Entity<Person>(entity =>
        {
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.ToTable("people");
            // Not unique: different actors can share a name. The provider's id tells them apart.
            entity.HasIndex(p => p.Name);
            entity.HasIndex(p => p.ExternalId).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
            entity.Property(p => p.Name).HasMaxLength(256);
            entity.Property(p => p.ExternalId).HasMaxLength(64);
            entity.Property(p => p.Gender).HasMaxLength(32);
            entity.Property(p => p.Country).HasMaxLength(128);
            entity.Property(p => p.CareerNotableShows).HasDefaultValueSql("'{}'");
        });

        modelBuilder.Entity<ShowCreative>(entity =>
        {
            entity.ToTable("show_creatives");
            entity.HasKey(c => new { c.ShowId, c.PersonId });
            entity.HasIndex(c => c.PersonId);
            entity.HasOne(c => c.Person).WithMany().HasForeignKey(c => c.PersonId);
        });

        modelBuilder.Entity<CatalogOverride>(entity =>
        {
            entity.ToTable("catalog_overrides");
            entity.Property(o => o.Id).ValueGeneratedNever();
            entity.HasIndex(o => o.ShowId);
            entity.Property(o => o.Field).HasMaxLength(32);
            entity.Property(o => o.Value).HasMaxLength(4000);
        });

        modelBuilder.Entity<Nanogenre>(entity =>
        {
            entity.ToTable("nanogenres");
            entity.HasKey(n => n.Slug);
            entity.Property(n => n.Slug).HasMaxLength(96);
            entity.Property(n => n.Name).HasMaxLength(96);
            entity.Property(n => n.Source).HasMaxLength(16);
        });

        modelBuilder.Entity<PersonCastCredit>(entity =>
        {
            entity.ToTable("person_cast_credits");
            entity.HasKey(c => new { c.PersonId, c.ShowExternalId });
            entity.HasOne<Person>().WithMany().HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(c => c.ShowExternalId).HasMaxLength(64);
            entity.Property(c => c.Title).HasMaxLength(256);
            entity.Property(c => c.Character).HasMaxLength(512);
        });

        modelBuilder.Entity<ShowQuoteSource>(entity =>
        {
            entity.ToTable("show_quote_sources");
            entity.HasKey(q => q.ShowId);
            entity.HasOne<Show>().WithOne().HasForeignKey<ShowQuoteSource>(q => q.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(q => q.Page).HasMaxLength(256);
        });

        modelBuilder.Entity<Quote>(entity =>
        {
            entity.ToTable("quotes");
            entity.Property(q => q.Id).ValueGeneratedNever();
            entity.HasOne<Show>().WithMany().HasForeignKey(q => q.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(q => new { q.ShowId, q.SeasonNumber, q.EpisodeNumber });
        });

        modelBuilder.Entity<ShowSetting>(entity =>
        {
            entity.ToTable("show_settings");
            entity.HasKey(s => new { s.ShowId, s.PlaceId, s.Kind });
            entity.Property(s => s.Kind).HasMaxLength(8).HasDefaultValue(ShowSettingKinds.Set);
            entity.HasOne<Show>().WithMany().HasForeignKey(s => s.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(s => s.PlaceId).HasMaxLength(32);
            entity.Property(s => s.Name).HasMaxLength(256);
            entity.Property(s => s.Country).HasMaxLength(128);
            entity.HasIndex(s => s.PlaceId);
        });

        modelBuilder.Entity<ShowSettingLookup>(entity =>
        {
            entity.ToTable("show_setting_lookups");
            entity.HasKey(s => s.ShowId);
            entity.HasOne<Show>().WithOne().HasForeignKey<ShowSettingLookup>(s => s.ShowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CastMember>(entity =>
        {
            entity.ToTable("cast_members");
            entity.HasKey(c => new { c.ShowId, c.PersonId });
            entity.HasOne(c => c.Person).WithMany().HasForeignKey(c => c.PersonId);
            entity.Property(c => c.Character).HasMaxLength(256);
            entity.Property(c => c.CharacterExternalIds).HasDefaultValueSql("'{}'");
        });

        modelBuilder.Entity<WatchProvider>(entity =>
        {
            entity.Property(w => w.Id).ValueGeneratedNever();
            entity.ToTable("watch_providers");
            entity.HasIndex(w => new { w.ShowId, w.Region });
            entity.Property(w => w.Region).HasMaxLength(2);
            entity.Property(w => w.ProviderName).HasMaxLength(128);
        });

        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.Entity<ShowEmbedding>(entity =>
        {
            entity.ToTable("show_embeddings");
            entity.HasKey(e => e.ShowId);
            entity.HasOne<Show>().WithOne().HasForeignKey<ShowEmbedding>(e => e.ShowId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Model).HasMaxLength(64);
            entity.Property(e => e.SourceHash).HasMaxLength(64);
            entity.Property(e => e.Embedding).HasColumnType($"vector({ShowEmbedding.Dimensions})");
            // Approximate nearest-neighbour index for cosine distance (the vectors are normalized).
            entity.HasIndex(e => e.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

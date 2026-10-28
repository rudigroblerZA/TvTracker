using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Research.API.Data;

/// <summary>A show being researched: the working state between saves.</summary>
public class Dossier
{
    public Guid ShowId { get; set; }

    public required string ShowTitle { get; set; }

    /// <summary>The insight as last drafted or edited, before it's saved.</summary>
    public string? Draft { get; set; }

    /// <summary>Set while the model drafts in the background (minutes on a CPU); cleared when it's done.</summary>
    public DateTime? DraftingSinceUtc { get; set; }

    /// <summary>Why the last draft failed, until the next one starts.</summary>
    public string? DraftProblem { get; set; }

    public DateTime? GatheredAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Something read for the research: a Wikidata item, a Wikipedia article, a TMDB record or a page a researcher added.</summary>
public class ResearchSource
{
    /// <summary>Set by the app.</summary>
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>One of <see cref="SourceKinds"/>.</summary>
    public required string Kind { get; set; }

    public required string Url { get; set; }

    public required string Title { get; set; }

    /// <summary>The text read from it, at most <see cref="MaxExcerpt"/> characters: what the model drafts from.</summary>
    public required string Excerpt { get; set; }

    /// <summary>Left out of drafts and saves while false, without losing it.</summary>
    public bool Included { get; set; } = true;

    public DateTime RetrievedAtUtc { get; set; }

    public const int MaxExcerpt = 6000;
}

/// <summary>A value a source offers for a fact; the researcher picks one per fact when saving.</summary>
public class FactCandidate
{
    /// <summary>Set by the app.</summary>
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    public Guid SourceId { get; set; }

    /// <summary>One of <see cref="ResearchFacts"/>' keys.</summary>
    public required string Key { get; set; }

    public required string Value { get; set; }
}

/// <summary>
/// A saved version of a show's research. The latest is the trusted record: what the show page and the MCP tools show,
/// and the source of the catalog fields it overrides. Earlier versions stay as history.
/// </summary>
public class SavedResearch
{
    /// <summary>Set by the app.</summary>
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    public int Version { get; set; }

    public required string Insight { get; set; }

    /// <summary>JSON: the chosen facts (<c>SavedFact[]</c>).</summary>
    public required string Facts { get; set; }

    /// <summary>JSON: the sources as they were when saved, numbered as the insight cites them (<c>SavedSource[]</c>).</summary>
    public required string Sources { get; set; }

    public DateTime SavedAtUtc { get; set; }

    /// <summary>Keycloak id of the moderator who saved it; cleared when their account is deleted.</summary>
    public string? SavedById { get; set; }

    public required string SavedByName { get; set; }
}

/// <summary>
/// A moderator's note on an episode: essential or skippable (<see cref="TvTracker.IntegrationEvents.EpisodeMarks"/>),
/// why, and where they read it. Analytics keeps a copy for the essential cut, which follows the notes before ratings.
/// </summary>
public class EpisodeNote
{
    /// <summary>The catalog's episode id; one note per episode.</summary>
    public Guid EpisodeId { get; set; }

    public Guid ShowId { get; set; }

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public required string Mark { get; set; }

    public required string Reason { get; set; }

    public required string SourceUrl { get; set; }

    public DateTime NotedAtUtc { get; set; }

    /// <summary>Keycloak id of the moderator who wrote it; cleared when their account is deleted.</summary>
    public string? NotedById { get; set; }

    public required string NotedByName { get; set; }

    public const int MaxReason = 280;
}

public static class SourceKinds
{
    public const string Wikidata = "wikidata";
    public const string Wikipedia = "wikipedia";
    public const string Tmdb = "tmdb";
    public const string Url = "url";

    /// <summary>Kinds a gather replaces; pages added by hand stay.</summary>
    public static readonly IReadOnlySet<string> Gathered = new HashSet<string> { Wikidata, Wikipedia, Tmdb };
}

public class ResearchContext(DbContextOptions<ResearchContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<Dossier> Dossiers => Set<Dossier>();
    public DbSet<ResearchSource> Sources => Set<ResearchSource>();
    public DbSet<FactCandidate> Candidates => Set<FactCandidate>();
    public DbSet<SavedResearch> Saved => Set<SavedResearch>();
    public DbSet<EpisodeNote> EpisodeNotes => Set<EpisodeNote>();
    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Dossier>(entity =>
        {
            entity.ToTable("dossiers");
            entity.HasKey(d => d.ShowId);
            entity.Property(d => d.ShowId).ValueGeneratedNever();
            entity.Property(d => d.ShowTitle).HasMaxLength(256);
            entity.Property(d => d.DraftProblem).HasMaxLength(500);
        });

        modelBuilder.Entity<ResearchSource>(entity =>
        {
            entity.ToTable("sources");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Kind).HasMaxLength(32);
            entity.Property(s => s.Url).HasMaxLength(2048);
            entity.Property(s => s.Title).HasMaxLength(512);
            entity.HasIndex(s => s.ShowId);
        });

        modelBuilder.Entity<FactCandidate>(entity =>
        {
            entity.ToTable("fact_candidates");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.Property(c => c.Key).HasMaxLength(32);
            entity.HasIndex(c => c.ShowId);
        });

        modelBuilder.Entity<SavedResearch>(entity =>
        {
            entity.ToTable("saved_research");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Facts).HasColumnType("jsonb");
            entity.Property(s => s.Sources).HasColumnType("jsonb");
            entity.Property(s => s.SavedById).HasMaxLength(64);
            entity.Property(s => s.SavedByName).HasMaxLength(256);
            entity.HasIndex(s => new { s.ShowId, s.Version }).IsUnique();
        });

        modelBuilder.Entity<EpisodeNote>(entity =>
        {
            entity.ToTable("episode_notes");
            entity.HasKey(n => n.EpisodeId);
            entity.Property(n => n.EpisodeId).ValueGeneratedNever();
            entity.Property(n => n.Mark).HasMaxLength(16);
            entity.Property(n => n.Reason).HasMaxLength(EpisodeNote.MaxReason);
            entity.Property(n => n.SourceUrl).HasMaxLength(2048);
            entity.Property(n => n.NotedById).HasMaxLength(64);
            entity.Property(n => n.NotedByName).HasMaxLength(256);
            entity.HasIndex(n => n.ShowId);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}

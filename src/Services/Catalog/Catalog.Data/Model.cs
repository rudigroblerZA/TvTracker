using Pgvector;
using TvTracker.IntegrationEvents;

namespace Catalog.Data;

public class Show
{
    public Guid Id { get; set; }

    /// <summary>Which provider the show came from ("mock", "tmdb", "tvmaze").</summary>
    public required string Source { get; set; }

    public required string ExternalId { get; set; }

    public required string Title { get; set; }

    public string? Overview { get; set; }

    public string? PosterUrl { get; set; }

    public string? BackdropUrl { get; set; }

    public DateOnly? FirstAirDate { get; set; }

    public ShowStatus Status { get; set; }

    public string? Network { get; set; }

    public List<string> Genres { get; set; } = [];

    public List<string> Keywords { get; set; } = [];

    public double Popularity { get; set; }

    /// <summary>The provider's average rating (0-10, e.g. TVmaze's), or null if it has none.</summary>
    public double? ExternalRating { get; set; }

    /// <summary>
    /// Analytics' tags for the show ("bingeable", "fan-favorite", …), kept current from its events so search can filter
    /// by them. Empty until Analytics has computed the show.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>The provider's "similar shows", as provider ids.</summary>
    public List<string> SimilarExternalIds { get; set; } = [];

    /// <summary>The similar shows that exist in this catalog, resolved from <see cref="SimilarExternalIds"/>.</summary>
    public List<Guid> SimilarShowIds { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastSyncedAtUtc { get; set; }

    /// <summary>When the sync worker should next refresh the show from the provider.</summary>
    public DateTime NextRefreshAtUtc { get; set; }

    public List<Season> Seasons { get; set; } = [];

    public List<Episode> Episodes { get; set; } = [];

    public List<CastMember> Cast { get; set; } = [];

    public List<WatchProvider> WatchProviders { get; set; } = [];
}

/// <summary>
/// A show's text (title, genres, network, summary) as a vector, for semantic search. Written by the sync worker,
/// which re-embeds a show when the text changes or the model does.
/// </summary>
public class ShowEmbedding
{
    public Guid ShowId { get; set; }

    /// <summary>The embedding model that produced <see cref="Embedding"/>; vectors from different models can't be compared.</summary>
    public required string Model { get; set; }

    public required Vector Embedding { get; set; }

    /// <summary>A hash of the text that was embedded, so a refresh that changes nothing doesn't re-embed.</summary>
    public required string SourceHash { get; set; }

    public DateTime EmbeddedAtUtc { get; set; }
}

public class Season
{
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    public int Number { get; set; }

    public required string Name { get; set; }

    public string? Overview { get; set; }

    public DateTimeOffset? PremieresAtUtc { get; set; }
}

public class Episode
{
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    public required string ExternalId { get; set; }

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public required string Title { get; set; }

    public string? Overview { get; set; }

    /// <summary>Air time in UTC; null until scheduled.</summary>
    public DateTimeOffset? AirsAtUtc { get; set; }

    public int? RuntimeMinutes { get; set; }

    /// <summary>The provider's average rating (0-10), or null if it has none (e.g. not aired yet).</summary>
    public double? ExternalRating { get; set; }

    /// <summary>Set once EpisodeAired has been published, so it is sent only once.</summary>
    public bool AiredPublished { get; set; }
}

public class Person
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? PhotoUrl { get; set; }

    /// <summary>The provider's id for the person (TVmaze's), so two actors with the same name stay apart.</summary>
    public string? ExternalId { get; set; }

    public DateOnly? Birthday { get; set; }

    public DateOnly? Deathday { get; set; }

    public string? Gender { get; set; }

    /// <summary>Country name, e.g. "United States".</summary>
    public string? Country { get; set; }
}

public class CastMember
{
    public Guid ShowId { get; set; }

    public Guid PersonId { get; set; }

    public Person Person { get; set; } = null!;

    public string? Character { get; set; }

    /// <summary>
    /// The provider's ids for the characters this person plays in the show. The same character id in
    /// two shows' casts is a crossover or spin-off.
    /// </summary>
    public List<string> CharacterExternalIds { get; set; } = [];

    public int Order { get; set; }
}

public enum WatchProviderType
{
    Stream,
    Free,
    Rent,
    Buy
}

public class WatchProvider
{
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>ISO 3166-1 country code.</summary>
    public required string Region { get; set; }

    public required string ProviderName { get; set; }

    public WatchProviderType Type { get; set; }

    public string? Url { get; set; }
}

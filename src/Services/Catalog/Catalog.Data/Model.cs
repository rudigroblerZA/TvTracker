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

    /// <summary>A large wide image (the provider's background art), for the show page's hero.</summary>
    public string? BackdropUrl { get; set; }

    /// <summary>A thin wide banner, usually with the show's logo (TVmaze's 758×140 "banner").</summary>
    public string? BannerUrl { get; set; }

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

    /// <summary>
    /// Slugs of the show's nanogenres (see <see cref="Nanogenre"/>), most specific first; kept so search can filter by
    /// them. Empty until the sync worker has discovered them, and for shows that fit none.
    /// </summary>
    public List<string> Nanogenres { get; set; } = [];

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

    /// <summary>Who made the show: its creators, developers and executive producers.</summary>
    public List<ShowCreative> Creatives { get; set; } = [];

    /// <summary>
    /// Whether <see cref="Creatives"/> came from the provider (it may still be empty). False for shows imported before
    /// creatives were; the sync worker backfills those.
    /// </summary>
    public bool CreativesImported { get; set; }

    public List<WatchProvider> WatchProviders { get; set; } = [];
}

/// <summary>
/// A fine-grained genre ("Sports comedy", "Animated family comedy") discovered by the sync worker from the catalog
/// itself: shows clustered by their text embeddings and genres, plus small groups sharing an uncommon genre pairing.
/// Rebuilt whole each time, so a slug can disappear when the catalog changes.
/// </summary>
public class Nanogenre
{
    public required string Slug { get; set; }

    public required string Name { get; set; }

    /// <summary>"cluster" (found by clustering) or "genres" (a small group sharing a genre pairing).</summary>
    public required string Source { get; set; }

    public int ShowCount { get; set; }

    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>
/// A show's text (title, genres, network, summary) as a vector, for semantic search. Written by the sync worker,
/// which re-embeds a show when the text changes or the model does.
/// </summary>
public class ShowEmbedding
{
    /// <summary>The vector column's size; the embedding model has to match it.</summary>
    public const int Dimensions = 384;

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

    /// <summary>Shows they're credited on behind the camera, across the provider (not just this catalog); null until looked up.</summary>
    public int? CareerShows { get; set; }

    /// <summary>Shows they created or developed, across the provider.</summary>
    public int? CareerCreated { get; set; }

    /// <summary>Shows they were an executive producer on, across the provider.</summary>
    public int? CareerExecutiveProduced { get; set; }

    /// <summary>Their best-known shows behind the camera, most popular first (titles; many aren't in this catalog).</summary>
    public List<string> CareerNotableShows { get; set; } = [];

    /// <summary>When the career figures were last fetched from the provider.</summary>
    public DateTime? CareerRefreshedAtUtc { get; set; }

    /// <summary>When <see cref="PersonCastCredit"/>s were last fetched for them (actors in catalog shows only).</summary>
    public DateTime? CastCreditsRefreshedAtUtc { get; set; }
}

/// <summary>
/// A show someone is in the main cast of, anywhere on the provider (most aren't in this catalog): what the reunion
/// radar finds actors working together again in. Replaced whole when the person's credits are refreshed.
/// </summary>
public class PersonCastCredit
{
    public Guid PersonId { get; set; }

    /// <summary>The provider's id for the show.</summary>
    public required string ShowExternalId { get; set; }

    /// <summary>The catalog's id, when the show was in the catalog at the last refresh.</summary>
    public Guid? ShowId { get; set; }

    public required string Title { get; set; }

    public string? PosterUrl { get; set; }

    public ShowStatus Status { get; set; }

    public DateOnly? Premiered { get; set; }

    /// <summary>The provider's popularity (TVmaze's weight, 0-100).</summary>
    public double Popularity { get; set; }

    public double? Rating { get; set; }

    /// <summary>The character, or several joined with " / ".</summary>
    public string? Character { get; set; }

    public bool Voice { get; set; }
}

/// <summary>
/// A show's page on Wikiquote, found through Wikidata by the show's TVmaze id, and when its lines were last fetched.
/// A row with no <see cref="Page"/> means Wikidata knows no page; the lookup is repeated monthly.
/// </summary>
public class ShowQuoteSource
{
    public Guid ShowId { get; set; }

    /// <summary>The page's title on en.wikiquote.org ("Breaking Bad"), or null when the show has none.</summary>
    public string? Page { get; set; }

    public DateTime CheckedAtUtc { get; set; }

    /// <summary>When the page and its season pages were last read; null until the first time.</summary>
    public DateTime? FetchedAtUtc { get; set; }

    /// <summary>A page's address on Wikiquote, or its raw wikitext's.</summary>
    public static string PageUrl(string page, bool raw = false)
    {
        var title = Uri.EscapeDataString(page.Replace(' ', '_')).Replace("%28", "(").Replace("%29", ")");
        return raw ? $"https://en.wikiquote.org/w/index.php?title={title}&action=raw" : $"https://en.wikiquote.org/wiki/{title}";
    }
}

/// <summary>
/// One memorable line or exchange from Wikiquote (CC BY-SA), for a show or one of its episodes. Replaced whole for the
/// show when its pages are read again.
/// </summary>
public class Quote
{
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>The episode's numbers as Wikiquote heads it ("[1.01]"); both null for a line about the whole show.</summary>
    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    /// <summary>Order on the page, so an episode's lines read as Wikiquote lists them.</summary>
    public int Position { get; set; }

    /// <summary>Who says each line, matching <see cref="Lines"/> by index; empty for a stage direction or narration.</summary>
    public List<string> Speakers { get; set; } = [];

    public List<string> Lines { get; set; } = [];
}

/// <summary>
/// A place a show is set in (Wikidata's narrative location, P840) or filmed in (filming location, P915), from the item
/// with the show's TVmaze id: a place with coordinates on Earth. Replaced whole for the show at each lookup; a show with
/// none simply has no rows.
/// </summary>
public class ShowSetting
{
    public Guid ShowId { get; set; }

    /// <summary><see cref="ShowSettingKinds.Set"/> or <see cref="ShowSettingKinds.Filmed"/>; a city can be both.</summary>
    public string Kind { get; set; } = ShowSettingKinds.Set;

    /// <summary>The place's Wikidata item ("Q34804" for Albuquerque).</summary>
    public required string PlaceId { get; set; }

    public required string Name { get; set; }

    /// <summary>The place's country, when Wikidata gives one in English.</summary>
    public string? Country { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }
}

public static class ShowSettingKinds
{
    public const string Set = "set";
    public const string Filmed = "filmed";
}

/// <summary>When a show's settings were last looked up on Wikidata, found or not.</summary>
public class ShowSettingLookup
{
    public Guid ShowId { get; set; }

    public DateTime CheckedAtUtc { get; set; }
}

/// <summary>A person who made a show, with their roles on it.</summary>
public class ShowCreative
{
    public Guid ShowId { get; set; }

    public Guid PersonId { get; set; }

    public Person Person { get; set; } = null!;

    /// <summary>"Creator", "Developer" and/or "Executive Producer".</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Creators and developers first, as the provider lists them.</summary>
    public int Order { get; set; }
}

public class CastMember
{
    public Guid ShowId { get; set; }

    public Guid PersonId { get; set; }

    public Person Person { get; set; } = null!;

    public string? Character { get; set; }

    /// <summary>
    /// The provider's ids for the characters this person plays in the show. TVmaze's are per show, even for the same
    /// character in a spin-off, so crossovers are found by actor and character name instead (Catalog.API's Universes).
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

/// <summary>
/// A community correction a moderator accepted (in Ratings.API): a value that replaces the provider's for one field,
/// applied on every sync so the next refresh doesn't undo it. Removing it lets the provider's value back in.
/// </summary>
public class CatalogOverride
{
    /// <summary>The correction's id in Ratings.API; set by the app.</summary>
    public Guid Id { get; set; }

    public Guid ShowId { get; set; }

    /// <summary>Null for a show field.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>One of <see cref="CatalogOverrideFields"/>.</summary>
    public required string Field { get; set; }

    public required string Value { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>The fields a correction or saved research can override, and how each value is written.</summary>
public static class CatalogOverrideFields
{
    /// <summary>The show's summary (plain text).</summary>
    public const string ShowSummary = "show-summary";

    /// <summary>The show's network or streaming service, up to 128 characters.</summary>
    public const string ShowNetwork = "show-network";

    /// <summary>The show's premiere date, yyyy-MM-dd.</summary>
    public const string ShowPremiered = "show-premiered";

    public const string EpisodeTitle = "episode-title";

    /// <summary>The episode's summary (plain text).</summary>
    public const string EpisodeSummary = "episode-summary";

    /// <summary>yyyy-MM-dd; the provider's time of day is kept (UTC midnight when it has none).</summary>
    public const string EpisodeAirDate = "episode-airdate";

    /// <summary>Whole minutes, 1-600.</summary>
    public const string EpisodeRuntime = "episode-runtime";

    public static readonly IReadOnlySet<string> ShowFields = new HashSet<string> { ShowSummary, ShowNetwork, ShowPremiered };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(ShowFields) { EpisodeTitle, EpisodeSummary, EpisodeAirDate, EpisodeRuntime };

    public static bool IsEpisodeField(string field) => !ShowFields.Contains(field);
}

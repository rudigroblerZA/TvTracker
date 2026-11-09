using System.Globalization;
using System.Text.RegularExpressions;
using Catalog.Data;
using TvTracker.IntegrationEvents;

namespace Catalog.SyncWorker.Providers.Wikidata;

/// <summary>
/// How Wikidata's statements read as the catalog's fields, kept apart from the queries so the rules can be tested.
/// Wikidata describes a show with items (genres such as "crime drama television series", types such as "animated
/// series"), where TVmaze gives a short fixed list; the catalog's genres, kinds and popularity are TVmaze's, so other
/// features (nanogenres, recommendations, the story-kind filters) read a Wikidata show the same way.
/// </summary>
public static partial class WikidataMapping
{
    /// <summary>
    /// The items a show is an instance of (P31): television series, miniseries, animated series, animated television
    /// series, anime television series and web series.
    /// </summary>
    public static readonly IReadOnlyList<string> ShowTypes = ["Q5398426", "Q1259759", "Q581714", "Q117467246", "Q63952888", "Q526877"];

    private static readonly HashSet<string> AnimatedTypes = ["Q581714", "Q117467246", "Q63952888"];

    /// <summary>
    /// Words in a Wikidata genre's label and the TVmaze genre each stands for. A label can give several ("comedy-drama",
    /// "medical drama"); matched at the start of a word, so "war" isn't found in "software".
    /// </summary>
    private static readonly (string Word, string Genre)[] GenreWords =
    [
        ("science fiction", "Science-Fiction"), ("sci-fi", "Science-Fiction"), ("comedy", "Comedy"), ("sitcom", "Comedy"),
        ("dramedy", "Comedy"), ("dramedy", "Drama"), ("drama", "Drama"), ("soap opera", "Drama"), ("crime", "Crime"),
        ("police procedural", "Crime"), ("detective", "Crime"), ("thriller", "Thriller"), ("mystery", "Mystery"),
        ("fantasy", "Fantasy"), ("horror", "Horror"), ("action", "Action"), ("adventure", "Adventure"),
        ("romance", "Romance"), ("romantic", "Romance"), ("family", "Family"), ("war", "War"), ("western", "Western"),
        ("historical", "History"), ("period", "History"), ("medical", "Medical"), ("legal", "Legal"),
        ("supernatural", "Supernatural"), ("espionage", "Espionage"), ("spy", "Espionage"), ("music", "Music"),
        ("sport", "Sports"), ("children", "Children"), ("anime", "Anime"), ("cooking", "Food"), ("food", "Food"),
        ("travel", "Travel"), ("nature", "Nature")
    ];

    /// <summary>
    /// A kind word in a genre's label and the TVmaze type it gives; the first that any label has wins, and a show with
    /// none is scripted.
    /// </summary>
    private static readonly (string Word, string Kind)[] KindWords =
    [
        ("animat", "animation"), ("anime", "animation"), ("cartoon", "animation"), ("reality", "reality"),
        ("documentary", "documentary"), ("docuseries", "documentary"), ("talk show", "talk show"),
        ("game show", "game show"), ("quiz", "game show"), ("news", "news"), ("variety", "variety")
    ];

    /// <summary>
    /// The item id at the end of an entity address ("http://www.wikidata.org/entity/Q1079" is "Q1079").
    /// </summary>
    public static string Id(string uri) => uri[(uri.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Whether a text is an item id, so it can go into a query as <c>wd:Q…</c>.
    /// </summary>
    public static bool IsItemId(string id) => ItemId().IsMatch(id);

    /// <summary>
    /// The date of a Wikidata time ("2008-01-20T00:00:00Z"); null for a date before year 1 or one that doesn't read.
    /// Wikidata stores a year-only date as 1 January, so a premiere can read early.
    /// </summary>
    public static DateOnly? Date(string? value) =>
        value is { Length: >= 10 } && value[0] != '-'
        && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// TVmaze's genres for Wikidata's genre labels, in TVmaze's spelling, each once.
    /// </summary>
    public static IReadOnlyList<string> Genres(IEnumerable<string> labels) =>
        labels
            .Select(label => label.ToLowerInvariant())
            .SelectMany(label => GenreWords.Where(g => Regex.IsMatch(label, $@"\b{Regex.Escape(g.Word)}")).Select(g => g.Genre))
            .Distinct()
            .ToList();

    /// <summary>
    /// The show's kind as TVmaze's type, lower case: animation for an animated type or genre, else the first kind word
    /// in a genre, else scripted.
    /// </summary>
    public static string Kind(IEnumerable<string> types, IEnumerable<string> genreLabels)
    {
        if (types.Any(AnimatedTypes.Contains))
        {
            return "animation";
        }
        var labels = genreLabels.Select(l => l.ToLowerInvariant()).ToList();
        // At the start of a word not joined to another, so a mockumentary or "pseudo-documentary" (The Office) isn't a
        // documentary.
        return KindWords.FirstOrDefault(k => labels.Any(l => Regex.IsMatch(l, $@"(?<![\w-]){Regex.Escape(k.Word)}"))).Kind ?? "scripted";
    }

    /// <summary>
    /// The language as TVmaze's keyword: the language's last word, lower case, so "American English" is "english"
    /// as TVmaze writes it, and "Brazilian Portuguese" "portuguese".
    /// </summary>
    public static string? Language(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim().Split(' ')[^1].ToLowerInvariant();

    /// <summary>
    /// TVmaze's 0-100 popularity from the number of Wikipedias (and other wikis) with a page about the show: 1.5 a
    /// page, capped at 100. Breaking Bad has about 100 pages, a well-known show 40 to 70, an obscure one a handful.
    /// </summary>
    public static double Popularity(int sitelinks) => Math.Min(100, sitelinks * 1.5);

    /// <summary>
    /// Started and finished by its start (P580) and end (P582) dates. A show with no end date is airing once it has
    /// started; the synchronizer presumes it ended after four quiet years, as it does with TVmaze's.
    /// </summary>
    public static ShowStatus Status(DateOnly? start, DateOnly? end, DateOnly today) =>
        end is { } e && e <= today ? ShowStatus.Ended
        : start is null || start > today ? ShowStatus.Upcoming
        : ShowStatus.Airing;

    /// <summary>
    /// TVmaze's gender words for Wikidata's male and female items; anything else is left out.
    /// </summary>
    public static string? Gender(string? item) => item is null ? null : Id(item) switch
    {
        "Q6581097" => "Male",
        "Q6581072" => "Female",
        _ => null
    };

    /// <summary>
    /// A Commons file (Wikidata gives its <c>Special:FilePath</c> address) scaled to a width, over https.
    /// </summary>
    public static string? Image(string? filePath, int width) =>
        filePath is null ? null : filePath.Replace("http://", "https://") + $"?width={width}";

    /// <summary>
    /// A show's episodes from Wikidata's rows, each with its season's number (the season item's ordinal in the series)
    /// and its own number in the season. Episodes without both are left out, since the catalog places every episode in
    /// a season; where two items claim one place, the one with an air date and then the lower id is kept.
    /// </summary>
    public static IReadOnlyList<ProviderEpisode> Episodes(IEnumerable<WikidataEpisodeRow> rows) =>
        rows
            .Where(r => r is { Season: > 0, Number: > 0 })
            .GroupBy(r => (r.Season!.Value, r.Number!.Value))
            .Select(g => g.OrderBy(r => r.Aired is null).ThenBy(r => r.Id.Length).ThenBy(r => r.Id, StringComparer.Ordinal).First())
            .OrderBy(r => r.Season).ThenBy(r => r.Number)
            .Select(r => new ProviderEpisode(
                r.Id,
                r.Season!.Value,
                r.Number!.Value,
                string.IsNullOrWhiteSpace(r.Title) || r.Title == r.Id ? $"Episode {r.Number}" : r.Title,
                null,
                // Wikidata gives the day, not the time: an episode counts as aired from the start of that day in UTC.
                r.Aired is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                r.Minutes is > 0 and <= 600 ? r.Minutes : null,
                null))
            .ToList();

    [GeneratedRegex("^Q[1-9][0-9]{0,11}$")]
    private static partial Regex ItemId();
}

/// <param name="Id">The episode's item id.</param>
/// <param name="Season">Its season's number in the series, when Wikidata gives one.</param>
/// <param name="Number">Its number in the season.</param>
public sealed record WikidataEpisodeRow(string Id, string? Title, int? Season, int? Number, DateOnly? Aired, int? Minutes);

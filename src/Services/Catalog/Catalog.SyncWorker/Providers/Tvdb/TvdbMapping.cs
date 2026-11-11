using System.Globalization;
using TvTracker.IntegrationEvents;

namespace Catalog.SyncWorker.Providers.Tvdb;

/// <summary>
/// TheTVDB's records read as the catalog's. Pure, so the rules are unit-tested without the API.
/// </summary>
public static class TvdbMapping
{
    public const int MaxCast = 20;
    private const string ArtworkHost = "https://artworks.thetvdb.com";
    private const int BannerArt = 1;
    private const int BackgroundArt = 3;

    /// <summary>
    /// TheTVDB lists a show's format among its genres; the first of these is its kind, as the other providers give it.
    /// </summary>
    private static readonly (string Genre, string Kind)[] KindGenres =
    [
        ("Animation", "animation"), ("Anime", "animation"), ("Reality", "reality"), ("Documentary", "documentary"),
        ("Talk Show", "talk show"), ("Game Show", "game show"), ("News", "news")
    ];

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "english", ["jpn"] = "japanese", ["kor"] = "korean", ["spa"] = "spanish", ["fra"] = "french",
        ["deu"] = "german", ["ita"] = "italian", ["por"] = "portuguese", ["zho"] = "chinese", ["hin"] = "hindi",
        ["swe"] = "swedish", ["dan"] = "danish", ["nor"] = "norwegian", ["nld"] = "dutch", ["tur"] = "turkish",
        ["rus"] = "russian", ["pol"] = "polish", ["fin"] = "finnish", ["heb"] = "hebrew", ["ara"] = "arabic"
    };

    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usa"] = "US", ["gbr"] = "GB", ["can"] = "CA", ["aus"] = "AU", ["nzl"] = "NZ", ["irl"] = "IE", ["jpn"] = "JP",
        ["kor"] = "KR", ["deu"] = "DE", ["fra"] = "FR", ["esp"] = "ES", ["ita"] = "IT", ["swe"] = "SE", ["dan"] = "DK",
        ["dnk"] = "DK", ["nor"] = "NO", ["nld"] = "NL", ["bel"] = "BE", ["ind"] = "IN", ["bra"] = "BR", ["mex"] = "MX",
        ["zaf"] = "ZA", ["chn"] = "CN", ["tur"] = "TR", ["pol"] = "PL", ["fin"] = "FI", ["isr"] = "IL"
    };

    /// <summary>
    /// A TheTVDB id: a positive whole number.
    /// </summary>
    public static bool IsId(string id) => id.Length is > 0 and <= 12 && id.All(char.IsAsciiDigit) && id[0] != '0';

    public static DateOnly? Date(string? value) =>
        value is { Length: >= 10 }
        && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// The address of an image: TheTVDB gives some in full and some as a path on its artwork host.
    /// </summary>
    public static string? Image(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null
        : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path.Replace("http://", "https://")
        : ArtworkHost + (path[0] == '/' ? path : "/" + path);

    /// <summary>
    /// TheTVDB's score is an open-ended count that grows with a show's following (a handful for an obscure show,
    /// hundreds of thousands for the best known), so its logarithm is what fits the catalog's 0-100.
    /// </summary>
    public static double Popularity(double? score) =>
        score is > 0 ? Math.Min(100, Math.Round(Math.Log10(score.Value + 1) * 16, 1)) : 0;

    /// <summary>
    /// "Continuing" is airing once it has started; TheTVDB doesn't tell a renewal or a cancellation apart from those.
    /// </summary>
    public static ShowStatus Status(string? status, DateOnly? firstAired, DateOnly today) => status?.Trim().ToLowerInvariant() switch
    {
        "ended" => ShowStatus.Ended,
        "upcoming" => ShowStatus.Upcoming,
        _ => firstAired is null || firstAired > today ? ShowStatus.Upcoming : ShowStatus.Airing
    };

    /// <summary>
    /// TheTVDB's genres under the names the rest of the catalog uses (TVmaze's).
    /// </summary>
    public static IReadOnlyList<string> Genres(IEnumerable<TvdbGenre>? genres) =>
        (genres ?? [])
            .Select(g => g.Name?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name == "Science Fiction" ? "Science-Fiction" : name!)
            .Distinct()
            .ToList();

    public static string Kind(IEnumerable<TvdbGenre>? genres)
    {
        var names = (genres ?? []).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return KindGenres.FirstOrDefault(k => names.Contains(k.Genre)).Kind ?? "scripted";
    }

    /// <summary>
    /// The language's name as a keyword ("english"), from TheTVDB's three-letter code; null for one not listed here.
    /// </summary>
    public static string? Language(string? code) => code is not null && Languages.TryGetValue(code.Trim(), out var name) ? name : null;

    /// <summary>
    /// The two-letter country code from TheTVDB's three-letter one; null for one not listed here.
    /// </summary>
    public static string? Country(string? code) => code is not null && Countries.TryGetValue(code.Trim(), out var two) ? two : null;

    /// <summary>
    /// Regular episodes in order, one per season and number: specials (season 0) and unnumbered episodes are left out,
    /// as with the other providers.
    /// </summary>
    public static IReadOnlyList<ProviderEpisode> Episodes(IEnumerable<TvdbEpisode> episodes) =>
        episodes
            .Where(e => e is { SeasonNumber: > 0, Number: > 0 })
            .GroupBy(e => (e.SeasonNumber!.Value, e.Number!.Value))
            // TheTVDB can hold a duplicate of an episode: the dated one, then the older record.
            .Select(g => g.OrderBy(e => Date(e.Aired) is null).ThenBy(e => e.Id).First())
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number)
            .Select(e => new ProviderEpisode(
                e.Id.ToString(CultureInfo.InvariantCulture),
                e.SeasonNumber!.Value,
                e.Number!.Value,
                string.IsNullOrWhiteSpace(e.Name) ? $"Episode {e.Number}" : Truncate(e.Name.Trim()),
                string.IsNullOrWhiteSpace(e.Overview) ? null : e.Overview.Trim(),
                // TheTVDB gives the day, not the time: an episode counts as aired from the start of that day in UTC.
                Date(e.Aired) is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                e.Runtime is > 0 and <= 600 ? e.Runtime : null,
                null))
            .ToList();

    /// <summary>
    /// A season for each one with episodes, named as TheTVDB names it in aired order, else "Season N".
    /// </summary>
    public static IReadOnlyList<ProviderSeason> Seasons(IEnumerable<TvdbSeason>? seasons, IReadOnlyList<ProviderEpisode> episodes)
    {
        var names = (seasons ?? [])
            .Where(s => s.Type?.Type is null or "official" && !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Number)
            .ToDictionary(g => g.Key, g => g.First().Name!.Trim());
        return episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key)
            .Select(g => new ProviderSeason(g.Key, names.GetValueOrDefault(g.Key) is { } name ? Truncate(name) : $"Season {g.Key}", null,
                g.Min(e => e.AirsAtUtc)))
            .ToList();
    }

    /// <summary>
    /// The main cast: regulars ("Actor", not "Guest Star"), featured ones first, then in TheTVDB's order; an actor with
    /// several characters is one member.
    /// </summary>
    public static IReadOnlyList<ProviderCastMember> Cast(IEnumerable<TvdbCharacter>? characters) =>
        (characters ?? [])
            .Where(c => c.PeopleType == "Actor" && c.PeopleId is not null && !string.IsNullOrWhiteSpace(c.PersonName))
            .GroupBy(c => c.PeopleId!.Value)
            .OrderBy(g => !g.Any(c => c.IsFeatured == true))
            .ThenBy(g => g.Min(c => c.Sort ?? int.MaxValue))
            .ThenBy(g => g.Key)
            .Take(MaxCast)
            .Select(g => new ProviderCastMember(
                Truncate(g.First().PersonName!.Trim()),
                string.Join(" / ", g.Select(c => c.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct()) is { Length: > 0 } played
                    ? Truncate(played)
                    : null,
                Image(g.Select(c => c.PersonImgURL).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))
                    ?? g.Select(c => c.Image).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))),
                g.Select(c => c.Id.ToString(CultureInfo.InvariantCulture)).Distinct().Order(StringComparer.Ordinal).ToList(),
                Person(g.Key)))
            .ToList();

    /// <summary>
    /// The show's creators and executive producers, a person once with every role they have.
    /// </summary>
    public static IReadOnlyList<ProviderCreative> Creatives(IEnumerable<TvdbCharacter>? characters) =>
        (characters ?? [])
            .Where(c => CreativeRole(c.PeopleType) is not null && c.PeopleId is not null && !string.IsNullOrWhiteSpace(c.PersonName))
            .GroupBy(c => c.PeopleId!.Value)
            .OrderBy(g => g.First().PersonName, StringComparer.Ordinal)
            .Select(g => new ProviderCreative(
                Truncate(g.First().PersonName!.Trim()),
                Image(g.Select(c => c.PersonImgURL).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))),
                g.Select(c => CreativeRole(c.PeopleType)!).Distinct().OrderBy(r => r == "Executive Producer").ToList(),
                Person(g.Key)))
            .ToList();

    /// <summary>
    /// "Creator" or "Executive Producer" for a credit of that type; null for any other.
    /// </summary>
    public static string? CreativeRole(string? peopleType) => peopleType switch
    {
        "Creator" => "Creator",
        "Executive Producer" => "Executive Producer",
        _ => null
    };

    /// <summary>
    /// A show from its extended record and its episodes.
    /// </summary>
    public static ProviderShow Show(TvdbSeries series, IEnumerable<TvdbEpisode> allEpisodes, DateOnly today)
    {
        var episodes = Episodes(allEpisodes);
        var seasons = Seasons(series.Seasons, episodes);
        // A show without a first-aired date starts with its first dated episode.
        var firstAired = Date(series.FirstAired)
            ?? (seasons.Select(s => s.PremieresAtUtc).Min() is { } first ? DateOnly.FromDateTime(first.UtcDateTime) : null);
        return new ProviderShow(
            ExternalId: series.Id.ToString(CultureInfo.InvariantCulture),
            Title: Truncate(series.Name?.Trim() is { Length: > 0 } name ? name : $"Series {series.Id}"),
            Overview: string.IsNullOrWhiteSpace(series.Overview) ? null : series.Overview.Trim(),
            PosterUrl: Image(series.Image),
            BackdropUrl: Image(Best(series.Artworks, BackgroundArt)),
            FirstAirDate: firstAired,
            Status: Status(series.Status?.Name, firstAired, today),
            // Where it's on now, else where it started.
            Network: (series.LatestNetwork?.Name ?? series.OriginalNetwork?.Name)?.Trim() is { Length: > 0 } network ? Truncate(network) : null,
            Genres: Genres(series.Genres),
            Keywords: new[] { Kind(series.Genres), Language(series.OriginalLanguage) }.OfType<string>().ToList(),
            Popularity: Popularity(series.Score),
            Seasons: seasons,
            Episodes: episodes,
            Cast: Cast(series.Characters),
            WatchOptions: [],
            SimilarExternalIds: [],
            Rating: null,
            Creatives: Creatives(series.Characters),
            BannerUrl: Image(Best(series.Artworks, BannerArt)));
    }

    /// <summary>
    /// A search hit as a summary; search gives neither an end date nor a score.
    /// </summary>
    public static ProviderShowSummary? Summary(TvdbSearchResult hit) =>
        hit.TvdbId is { } id && IsId(id) && !string.IsNullOrWhiteSpace(hit.Name)
            ? new ProviderShowSummary(id, Truncate(hit.Name.Trim()), Image(hit.ImageUrl), Date(hit.FirstAirTime), null,
                string.IsNullOrWhiteSpace(hit.Network) ? null : hit.Network.Trim(), Country(hit.Country), Language(hit.PrimaryLanguage),
                null, 0, null)
            : null;

    /// <summary>
    /// The best-scored artwork of a type, preferring one in English or without text.
    /// </summary>
    private static string? Best(IEnumerable<TvdbArtwork>? artworks, int type) =>
        (artworks ?? [])
            .Where(a => a.Type == type && !string.IsNullOrWhiteSpace(a.Image))
            .OrderBy(a => a.Language is not (null or "" or "eng"))
            .ThenByDescending(a => a.Score ?? 0)
            .Select(a => a.Image)
            .FirstOrDefault();

    /// <summary>
    /// A person by id alone: a show's record has no birthday, gender or country for its people.
    /// </summary>
    private static ProviderPerson Person(long id) => new(id.ToString(CultureInfo.InvariantCulture), null, null, null, null);

    private static string Truncate(string text) => text.Length <= 256 ? text : text[..256];
}

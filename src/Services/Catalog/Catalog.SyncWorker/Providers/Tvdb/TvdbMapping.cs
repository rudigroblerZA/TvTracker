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
    /// The time zone a country's networks give air times in (the eastern one for the US and Canada, as schedules do).
    /// </summary>
    private static readonly Dictionary<string, string> TimeZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usa"] = "America/New_York", ["can"] = "America/Toronto", ["gbr"] = "Europe/London", ["irl"] = "Europe/Dublin",
        ["aus"] = "Australia/Sydney", ["nzl"] = "Pacific/Auckland", ["jpn"] = "Asia/Tokyo", ["kor"] = "Asia/Seoul",
        ["chn"] = "Asia/Shanghai", ["ind"] = "Asia/Kolkata", ["deu"] = "Europe/Berlin", ["fra"] = "Europe/Paris",
        ["esp"] = "Europe/Madrid", ["ita"] = "Europe/Rome", ["nld"] = "Europe/Amsterdam", ["bel"] = "Europe/Brussels",
        ["swe"] = "Europe/Stockholm", ["dnk"] = "Europe/Copenhagen", ["dan"] = "Europe/Copenhagen", ["nor"] = "Europe/Oslo",
        ["fin"] = "Europe/Helsinki", ["pol"] = "Europe/Warsaw", ["tur"] = "Europe/Istanbul", ["isr"] = "Asia/Jerusalem",
        ["bra"] = "America/Sao_Paulo", ["mex"] = "America/Mexico_City", ["zaf"] = "Africa/Johannesburg"
    };

    /// <summary>
    /// When nothing says what time a show airs: the evening, so an episode isn't taken as out a day before it is.
    /// </summary>
    private static readonly TimeOnly Evening = new(20, 0);

    /// <summary>
    /// When an episode airs, in UTC. TheTVDB gives the day per episode and one time for the series ("21:00"), in the
    /// time of the show's country.
    /// <list type="bullet">
    /// <item><b>No time given:</b> 20:00 there. Midnight would count a US evening episode as aired, and tell its
    /// followers, a day early.</item>
    /// <item><b>A country not listed here:</b> the time is taken as UTC.</item>
    /// </list>
    /// </summary>
    public static DateTimeOffset AirsAt(DateOnly day, string? airsTime, string? country)
    {
        var local = day.ToDateTime(
            TimeOnly.TryParseExact(airsTime?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var given)
                ? given
                : Evening);
        if (country is not null && TimeZones.TryGetValue(country.Trim(), out var zoneId))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                // A time skipped by the clocks going forward doesn't exist there: an hour later does.
                if (zone.IsInvalidTime(local))
                {
                    local = local.AddHours(1);
                }
                return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // A machine without that zone's data: as for a country not listed.
            }
        }
        return new DateTimeOffset(local, TimeSpan.Zero);
    }

    /// <summary>
    /// Regular episodes in order, one per season and number: specials (season 0) and unnumbered episodes are left out,
    /// as with the other providers. <paramref name="airsTime"/> and <paramref name="country"/> are the series'
    /// (<see cref="AirsAt"/>).
    /// </summary>
    public static IReadOnlyList<ProviderEpisode> Episodes(IEnumerable<TvdbEpisode> episodes, string? airsTime = null, string? country = null) =>
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
                Date(e.Aired) is { } day ? AirsAt(day, airsTime, country) : null,
                e.Runtime is > 0 and <= 600 ? e.Runtime : null,
                null))
            .ToList();

    /// <summary>
    /// A season for each one with episodes, named as TheTVDB names it in aired order, else "Season N".
    /// </summary>
    public static IReadOnlyList<ProviderSeason> Seasons(IEnumerable<TvdbSeason>? seasons, IReadOnlyList<ProviderEpisode> episodes)
    {
        var names = (seasons ?? [])
            .Where(s => s.Number is not null && s.Type?.Type is null or "official" && !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Number!.Value)
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
    /// A record's name in a language, from its translations; null when it has none in it.
    /// </summary>
    public static string? Translated(IEnumerable<TvdbTranslation>? translations, string language, Func<TvdbTranslation, string?> text) =>
        (translations ?? [])
            .Where(t => string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase))
            .Select(t => text(t)?.Trim())
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));

    /// <summary>
    /// Episodes with their name and overview in another language where that listing has them: an episode TheTVDB has
    /// no translation for keeps its original.
    /// </summary>
    public static IEnumerable<TvdbEpisode> WithTranslations(IEnumerable<TvdbEpisode> episodes, IEnumerable<TvdbEpisode> translated)
    {
        var byId = translated.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        return episodes.Select(e => byId.TryGetValue(e.Id, out var t)
            ? e with
            {
                Name = string.IsNullOrWhiteSpace(t.Name) ? e.Name : t.Name,
                Overview = string.IsNullOrWhiteSpace(t.Overview) ? e.Overview : t.Overview
            }
            : e);
    }

    /// <summary>
    /// A show from its extended record and its episodes. Its title and overview are in <paramref name="language"/>
    /// when the record carries that translation (asked for with <c>meta=translations</c>), else as TheTVDB has them.
    /// </summary>
    public static ProviderShow Show(TvdbSeries series, IEnumerable<TvdbEpisode> allEpisodes, DateOnly today, string language = "eng")
    {
        series = series with
        {
            Name = Translated(series.Translations?.NameTranslations, language, t => t.Name) ?? series.Name,
            Overview = Translated(series.Translations?.OverviewTranslations, language, t => t.Overview) ?? series.Overview
        };
        var episodes = Episodes(allEpisodes, series.AirsTime, series.OriginalCountry);
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
    /// A search hit as a summary, titled in <paramref name="language"/> when the hit has that translation; search
    /// gives neither an end date nor a score.
    /// </summary>
    public static ProviderShowSummary? Summary(TvdbSearchResult hit, string language = "eng") =>
        hit.TvdbId is { } id && IsId(id)
        && (hit.Translations?.GetValueOrDefault(language)?.Trim() is { Length: > 0 } translated ? translated : hit.Name?.Trim()) is { Length: > 0 } title
            ? new ProviderShowSummary(id, Truncate(title), Image(hit.ImageUrl), Date(hit.FirstAirTime), null,
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

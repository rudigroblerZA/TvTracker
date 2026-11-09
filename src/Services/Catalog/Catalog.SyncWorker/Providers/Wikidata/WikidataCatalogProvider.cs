using System.Globalization;
using Catalog.Data;
using TvTracker.IntegrationEvents;
using static Catalog.SyncWorker.Providers.Wikidata.WikidataMapping;

namespace Catalog.SyncWorker.Providers.Wikidata;

/// <summary>
/// The catalog from Wikidata (<c>Catalog:Provider=wikidata</c>), whose data is CC0: no licence terms at all, where
/// TVmaze's is CC BY-SA. Shows are items that are television series, miniseries or animated series; their seasons and
/// episodes are the episode items that are part of the series (P179), placed by their season (P4908) and its ordinal.
/// The overview is the English Wikipedia article's lead (CC BY-SA), else Wikidata's short description.
/// <para>
/// What it doesn't have: ratings, where to watch, similar shows, a change feed (refreshes go by the synchronizer's
/// timer) and air times (episodes have a day). Coverage of episodes is good for well-known shows and thin for the rest.
/// Every request goes through <see cref="WikidataSparql"/>, one at a time.
/// </para>
/// </summary>
public sealed class WikidataCatalogProvider(WikidataSparql wikidata, TimeProvider time, ILogger<WikidataCatalogProvider> logger) : ICatalogProvider
{
    private const int MaxCast = 20;
    private const int FeaturedCount = 100;
    private static readonly TimeSpan FeaturedFor = TimeSpan.FromDays(1);

    private (DateTimeOffset At, IReadOnlyList<string> Ids)? featured;

    public string Name => CatalogSources.Wikidata;

    private static string Types => string.Join(' ', ShowTypes.Select(t => "wd:" + t));

    /// <summary>
    /// The most popular shows, read once a day: the index query takes about 20 seconds.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetFeaturedShowIdsAsync(CancellationToken cancellationToken = default)
    {
        if (featured is { } f && time.GetUtcNow() - f.At < FeaturedFor)
        {
            return f.Ids;
        }
        var ids = await GetPopularShowIdsAsync(FeaturedCount, cancellationToken: cancellationToken);
        featured = (time.GetUtcNow(), ids);
        return ids;
    }

    /// <summary>
    /// By number of wikis with a page about the show. The floor keeps the query inside the service's 60 s limit: it
    /// scans every show with at least that many pages.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPopularShowIdsAsync(int count, Action<int>? onPage = null, CancellationToken cancellationToken = default)
    {
        var floor = count <= 200 ? 30 : count <= 1000 ? 15 : 8;
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?item ?sl WHERE {
              VALUES ?type { {{Types}} }
              ?item wdt:P31 ?type ; wikibase:sitelinks ?sl .
              FILTER(?sl >= {{floor}})
            } ORDER BY DESC(?sl) LIMIT {{count * 2}}
            """, cancellationToken);
        onPage?.Invoke(1);
        return rows.Select(r => Id(r["item"])).Distinct().Take(count).ToList();
    }

    public async Task<IReadOnlyList<string>> SearchShowIdsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        (await SearchAsync(query, cancellationToken)).Take(limit).ToList();

    public async Task<IReadOnlyList<ProviderShowSummary>> SearchShowsAsync(string query, CancellationToken cancellationToken = default)
    {
        var ids = await SearchAsync(query, cancellationToken);
        var summaries = (await SummariesAsync(ids, cancellationToken)).ToDictionary(s => s.Item);
        return ids.Where(summaries.ContainsKey).Select(id => summaries[id].ToSummary()).ToList();
    }

    /// <summary>
    /// Wikidata's own entity search (labels and aliases, best first), kept to shows.
    /// </summary>
    private async Task<List<string>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?item ?ordinal WHERE {
              SERVICE wikibase:mwapi {
                bd:serviceParam wikibase:api "EntitySearch"; wikibase:endpoint "www.wikidata.org";
                  mwapi:search {{WikidataSparql.Literal(query.Trim())}}; mwapi:language "en"; mwapi:limit "50".
                ?item wikibase:apiOutputItem mwapi:item.
                ?ordinal wikibase:apiOrdinal true.
              }
              FILTER EXISTS { VALUES ?type { {{Types}} } ?item wdt:P31 ?type }
            } ORDER BY ?ordinal
            """, ct);
        return rows.Select(r => Id(r["item"])).Distinct().ToList();
    }

    public Task<string?> LookUpShowByTvdbAsync(string tvdbId, CancellationToken cancellationToken = default) =>
        LookUpAsync("P4835", tvdbId, cancellationToken);

    public Task<string?> LookUpShowByImdbAsync(string imdbId, CancellationToken cancellationToken = default) =>
        LookUpAsync("P345", imdbId, cancellationToken);

    private async Task<string?> LookUpAsync(string property, string value, CancellationToken ct)
    {
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?item WHERE { ?item wdt:{{property}} {{WikidataSparql.Literal(value)}} . } LIMIT 1
            """, ct);
        return rows.Select(r => Id(r["item"])).FirstOrDefault();
    }

    /// <summary>
    /// Wikidata keeps a feed of recent changes, but not one by show with its episodes; refreshes go by the timer.
    /// </summary>
    public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetChangedShowsAsync(TimeSpan within, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(null);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetChangedPeopleAsync(TimeSpan within, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(null);

    public async Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (!IsItemId(externalId) || (await SummariesAsync([externalId], cancellationToken)).FirstOrDefault() is not { } show)
        {
            return null;
        }

        var episodes = Episodes((await wikidata.QueryAsync($$"""
            SELECT ?ep ?title (MIN(?s) AS ?season) (MIN(?n) AS ?number) (MIN(?date) AS ?aired) (MIN(?dur) AS ?minutes) WHERE {
              ?ep wdt:P179 wd:{{externalId}} ; wdt:P31 wd:Q21191270 .
              OPTIONAL {
                ?ep p:P4908 ?st . ?st ps:P4908 ?seasonItem .
                OPTIONAL { ?st pq:P1545 ?nRaw . BIND(xsd:integer(?nRaw) AS ?n) }
                OPTIONAL { ?seasonItem p:P179 ?ss . ?ss ps:P179 wd:{{externalId}} ; pq:P1545 ?sRaw . BIND(xsd:integer(?sRaw) AS ?s) }
              }
              OPTIONAL { ?ep wdt:P577 ?date }
              OPTIONAL { ?ep wdt:P2047 ?dur }
              OPTIONAL { ?ep rdfs:label ?title FILTER(LANG(?title) = "en") }
            } GROUP BY ?ep ?title
            """, cancellationToken))
            .Select(r => new WikidataEpisodeRow(Id(r["ep"]), r.GetValueOrDefault("title"), Int(r, "season"), Int(r, "number"),
                Date(r.GetValueOrDefault("aired")), Int(r, "minutes")))
            .GroupBy(r => r.Id)
            .Select(g => g.First()));

        var seasons = episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key)
            .Select(g => new ProviderSeason(g.Key, $"Season {g.Key}", null, g.Min(e => e.AirsAtUtc)))
            .ToList();

        // A show without a start date (P580) starts with its first dated episode.
        var firstAired = show.Start ?? (seasons.Select(s => s.PremieresAtUtc).Min() is { } first ? DateOnly.FromDateTime(first.UtcDateTime) : null);
        var people = await PeopleAsync(externalId, cancellationToken);
        var overview = show.Article is { } article
            ? await SummaryOrNullAsync(article, cancellationToken)
            : null;

        return new ProviderShow(
            ExternalId: externalId,
            Title: show.Title,
            Overview: overview ?? Capitalised(show.Description),
            PosterUrl: Image(show.Image, 680),
            BackdropUrl: null,
            FirstAirDate: firstAired,
            Status: Status(firstAired, show.End, Today),
            Network: show.Network,
            Genres: Genres(show.GenreLabels),
            Keywords: new[] { show.Kind, Language(show.Language) }.OfType<string>().ToList(),
            Popularity: Popularity(show.Sitelinks),
            Seasons: seasons,
            Episodes: episodes,
            Cast: people.Cast,
            WatchOptions: [],
            SimilarExternalIds: [],
            Rating: null,
            Creatives: people.Creatives);
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private async Task<string?> SummaryOrNullAsync(string article, CancellationToken ct)
    {
        try
        {
            return await wikidata.WikipediaSummaryAsync(article, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation(ex, "No Wikipedia summary for {Article}; using Wikidata's description", article);
            return null;
        }
    }

    /// <summary>
    /// The cast (P161, with the character qualifier P453) and the makers: creators (P170) and executive producers
    /// (P1431). Wikidata keeps no billing order, so the cast is ordered by how well known each actor is (wikis with a
    /// page about them), those with a stated character first: Breaking Bad's cast lists guest stars too, and John de
    /// Lancie led it by fame alone.
    /// </summary>
    private async Task<(List<ProviderCastMember> Cast, List<ProviderCreative> Creatives)> PeopleAsync(string showId, CancellationToken ct)
    {
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?role ?person ?name ?char ?charName ?birth ?death ?gender ?country ?photo ?sl WHERE {
              VALUES (?p ?ps ?role) { (p:P161 ps:P161 "cast") (p:P170 ps:P170 "Creator") (p:P1431 ps:P1431 "Executive Producer") }
              wd:{{showId}} ?p ?st . ?st ?ps ?person .
              ?person rdfs:label ?name FILTER(LANG(?name) = "en")
              ?person wikibase:sitelinks ?sl .
              OPTIONAL { ?st pq:P453 ?char . OPTIONAL { ?char rdfs:label ?charName FILTER(LANG(?charName) = "en") } }
              OPTIONAL { ?person wdt:P569 ?birth }
              OPTIONAL { ?person wdt:P570 ?death }
              OPTIONAL { ?person wdt:P21 ?gender }
              OPTIONAL { ?person wdt:P19/wdt:P17 ?c . ?c rdfs:label ?country FILTER(LANG(?country) = "en") }
              OPTIONAL { ?person wdt:P18 ?photo }
            }
            """, ct);

        // A person can have several of a statement (two birth dates, two countries); the least is taken, so a refresh
        // reads the same.
        ProviderPerson Person(IGrouping<string, Dictionary<string, string>> g) =>
            new(Id(g.Key), Date(Least(g, "birth")), Date(Least(g, "death")), Gender(Least(g, "gender")), Least(g, "country"));

        var cast = rows.Where(r => r["role"] == "cast")
            .GroupBy(r => r["person"])
            // Regulars usually have their character stated; guest stars often don't.
            .OrderBy(g => g.All(r => !r.ContainsKey("char")))
            .ThenByDescending(g => Int(g.First(), "sl"))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxCast)
            .Select(g => new ProviderCastMember(
                Truncate(g.First()["name"]),
                string.Join(" / ", g.Select(r => r.GetValueOrDefault("charName")).OfType<string>().Distinct().Order(StringComparer.Ordinal)) is { Length: > 0 } c ? Truncate(c) : null,
                Image(Least(g, "photo"), 300),
                // Wikidata's characters are items of their own, the same in every show they appear in.
                g.Select(r => r.GetValueOrDefault("char")).OfType<string>().Select(Id).Distinct().Order(StringComparer.Ordinal).ToList(),
                Person(g)))
            .ToList();

        var creatives = rows.Where(r => r["role"] != "cast")
            .GroupBy(r => r["person"])
            .OrderBy(g => g.First()["name"], StringComparer.Ordinal)
            .Select(g => new ProviderCreative(
                Truncate(g.First()["name"]),
                Image(Least(g, "photo"), 300),
                g.Select(r => r["role"]).Distinct().OrderBy(r => r == "Executive Producer").ToList(),
                Person(g)))
            .ToList();
        return (cast, creatives);
    }

    /// <summary>
    /// The shows a person created (P170) or was an executive producer of (P1431).
    /// </summary>
    public async Task<ProviderCareer?> GetCareerAsync(string personExternalId, CancellationToken cancellationToken = default)
    {
        if (!IsItemId(personExternalId))
        {
            return null;
        }
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?show ?role WHERE {
              VALUES (?prop ?role) { (wdt:P170 "Creator") (wdt:P1431 "Executive Producer") }
              ?show ?prop wd:{{personExternalId}} .
              FILTER EXISTS { VALUES ?type { {{Types}} } ?show wdt:P31 ?type }
            }
            """, cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }
        var roles = rows.GroupBy(r => Id(r["show"])).ToDictionary(g => g.Key, g => g.Select(r => r["role"]).Distinct().ToList());
        var summaries = await SummariesAsync(roles.Keys, cancellationToken);
        var made = summaries.Where(s => roles[s.Item].Contains("Creator"))
            .Select(s => s.ToMade(roles[s.Item].Where(r => r == "Creator").ToList(), time))
            .ToList();
        return new ProviderCareer(
            roles.Count,
            roles.Count(r => r.Value.Contains("Creator")),
            roles.Count(r => r.Value.Contains("Executive Producer")),
            summaries.OrderByDescending(s => s.Sitelinks).Take(10).Select(s => s.Title).ToList(),
            made);
    }

    /// <summary>
    /// The shows a person is in the cast of (P161), with the characters they play there.
    /// </summary>
    public async Task<IReadOnlyList<ProviderActingRole>?> GetCastCreditsAsync(string personExternalId, CancellationToken cancellationToken = default)
    {
        if (!IsItemId(personExternalId))
        {
            return null;
        }
        var rows = await wikidata.QueryAsync($$"""
            SELECT ?show ?charName WHERE {
              ?show p:P161 ?st . ?st ps:P161 wd:{{personExternalId}} .
              FILTER EXISTS { VALUES ?type { {{Types}} } ?show wdt:P31 ?type }
              OPTIONAL { ?st pq:P453 ?char . ?char rdfs:label ?charName FILTER(LANG(?charName) = "en") }
            }
            """, cancellationToken);
        var characters = rows.GroupBy(r => Id(r["show"]))
            .ToDictionary(g => g.Key, g => g.Select(r => r.GetValueOrDefault("charName")).OfType<string>().Distinct().ToList());
        var summaries = await SummariesAsync(characters.Keys, cancellationToken);
        return summaries
            .Select(s => new ProviderActingRole(s.Item, s.Title, Image(s.Image, 340), Status(s.Start, s.End, Today), s.Start,
                Popularity(s.Sitelinks), null, characters[s.Item] is { Count: > 0 } c ? string.Join(" / ", c) : null,
                s.Kind == "animation"))
            .ToList();
    }

    /// <summary>
    /// What a show item says about itself, for up to 100 at a time; items that aren't shows are left out.
    /// </summary>
    private async Task<List<WikidataShow>> SummariesAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var all = ids.Where(IsItemId).Distinct().ToList();
        var shows = new List<WikidataShow>();
        foreach (var batch in all.Chunk(100))
        {
            var rows = await wikidata.QueryAsync($$"""
                SELECT ?item ?label ?description ?sl ?article (MIN(?start) AS ?first) (MAX(?end) AS ?last)
                  (MIN(?image) AS ?img) (MIN(?networkName) AS ?network) (MIN(?code) AS ?country)
                  (MIN(?languageName) AS ?language)
                  (GROUP_CONCAT(DISTINCT STR(?type); separator = "|") AS ?types)
                  (GROUP_CONCAT(DISTINCT ?genreName; separator = "|") AS ?genres)
                WHERE {
                  VALUES ?item { {{string.Join(' ', batch.Select(id => "wd:" + id))}} }
                  VALUES ?showType { {{Types}} }
                  ?item wdt:P31 ?showType ; wdt:P31 ?type ; wikibase:sitelinks ?sl ; rdfs:label ?label FILTER(LANG(?label) = "en")
                  OPTIONAL { ?item schema:description ?description FILTER(LANG(?description) = "en") }
                  OPTIONAL { ?article schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> }
                  OPTIONAL { ?item wdt:P580 ?start }
                  OPTIONAL { ?item wdt:P582 ?end }
                  OPTIONAL { ?item wdt:P3383|wdt:P18|wdt:P154 ?image }
                  OPTIONAL { ?item wdt:P449 ?net . ?net rdfs:label ?networkName FILTER(LANG(?networkName) = "en") }
                  OPTIONAL { ?item wdt:P495/wdt:P297 ?code }
                  OPTIONAL { ?item wdt:P364 ?lang . ?lang rdfs:label ?languageName FILTER(LANG(?languageName) = "en") }
                  OPTIONAL { ?item wdt:P136 ?genre . ?genre rdfs:label ?genreName FILTER(LANG(?genreName) = "en") }
                } GROUP BY ?item ?label ?description ?sl ?article
                """, ct);
            shows.AddRange(rows.GroupBy(r => Id(r["item"])).Select(g => WikidataShow.From(g.First())));
        }
        return shows;
    }

    /// <summary>
    /// The least of a variable's values across rows: Wikidata answers in no fixed order, and the catalog sees any
    /// difference as a change.
    /// </summary>
    private static string? Least(IEnumerable<Dictionary<string, string>> rows, string key) =>
        rows.Select(r => r.GetValueOrDefault(key)).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();

    private static int? Int(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : null;

    private static string Truncate(string text) => text.Length <= 256 ? text : text[..256];

    private static string? Capitalised(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : char.ToUpperInvariant(text[0]) + text[1..] + ".";

    /// <summary>
    /// One show item's own statements.
    /// </summary>
    private sealed record WikidataShow(string Item, string Title, string? Description, string? Article, int Sitelinks,
        DateOnly? Start, DateOnly? End, string? Image, string? Network, string? Country, string? Language,
        IReadOnlyList<string> Types, IReadOnlyList<string> GenreLabels)
    {
        public string Kind => WikidataMapping.Kind(Types, GenreLabels);

        public static WikidataShow From(Dictionary<string, string> r) => new(
            Id(r["item"]), Truncate(r["label"]), r.GetValueOrDefault("description"), r.GetValueOrDefault("article"),
            Int(r, "sl") ?? 0, Date(r.GetValueOrDefault("first")), Date(r.GetValueOrDefault("last")), r.GetValueOrDefault("img"),
            r.GetValueOrDefault("network") is { } n ? Truncate(n) : null, r.GetValueOrDefault("country"), r.GetValueOrDefault("language"),
            Split(r.GetValueOrDefault("types")).Select(Id).ToList(), Split(r.GetValueOrDefault("genres")));

        /// <summary>
        /// Sorted: GROUP_CONCAT joins in no fixed order, so the genres would read as changed on every refresh.
        /// </summary>
        private static List<string> Split(string? joined) =>
            string.IsNullOrEmpty(joined) ? [] : joined.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToList();

        private static DateOnly TodayOf(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        public ProviderShowSummary ToSummary() =>
            new(Item, Title, WikidataMapping.Image(Image, 340), Start, End, Network, Country, Language, Kind, Popularity(Sitelinks), null);

        public ProviderMadeShow ToMade(IReadOnlyList<string> roles, TimeProvider time) =>
            new(Item, Title, WikidataMapping.Image(Image, 340), Start, End, Status(Start, End, TodayOf(time)), null,
                Popularity(Sitelinks), Kind, roles);
    }
}

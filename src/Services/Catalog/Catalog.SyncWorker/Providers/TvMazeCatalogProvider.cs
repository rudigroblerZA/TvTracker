using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Catalog.Data;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;

namespace Catalog.SyncWorker.Providers;

public sealed class TvMazeOptions
{
    public string BaseUrl { get; set; } = "https://api.tvmaze.com/";

    /// <summary>Countries whose broadcast schedule for today supplies featured shows, alongside the streaming schedule.</summary>
    public List<string> FeaturedCountries { get; set; } = ["US", "GB"];

    /// <summary>How many of today's most popular airing shows to import each sync.</summary>
    public int FeaturedCount { get; set; } = 40;

    /// <summary>Show types left out of the featured list (TVmaze's "type" field).</summary>
    public List<string> ExcludedFeaturedTypes { get; set; } = ["News", "Talk Show", "Sports", "Award Show", "Panel Show"];

    /// <summary>
    /// Leaves shows that air at least this many days a week (soaps, daily game shows) out of the featured list.
    /// They can have thousands of episodes, and importing one publishes an event per episode. 0 keeps them.
    /// </summary>
    public int ExcludeFeaturedAiringDaysPerWeek { get; set; } = 4;

    /// <summary>
    /// Regions a streaming service with no country (available worldwide, e.g. Netflix) is listed under.
    /// TVmaze has no per-region availability, so this is approximate.
    /// </summary>
    public List<string> WatchRegions { get; set; } = ["US", "GB", "ZA"];

    public int MaxCastMembers { get; set; } = 20;
}

/// <summary>
/// Catalog data from TVmaze (https://www.tvmaze.com/api), a free API licensed under CC BY-SA:
/// credit TVmaze wherever its data is shown. Rate limit is 20 calls every 10 seconds per IP;
/// <see cref="TvMazeRateLimitHandler"/> keeps requests under it and the standard resilience handler retries 429s.
/// TVmaze has no keywords or similar-show lists, so those stay empty; recommendations fall back to genres and cast.
/// </summary>
public sealed partial class TvMazeCatalogProvider(
    HttpClient http, IOptions<TvMazeOptions> options, TimeProvider time, ILogger<TvMazeCatalogProvider> logger)
    : ICatalogProvider
{
    private readonly TvMazeOptions _options = options.Value;

    public string Name => "tvmaze";

    public async Task<IReadOnlyList<string>> GetFeaturedShowIdsAsync(CancellationToken cancellationToken = default)
    {
        var airing = new List<TvMazeShow>();

        foreach (var country in _options.FeaturedCountries)
        {
            var schedule = await http.GetFromJsonAsync<List<TvMazeScheduleItem>>($"schedule?country={country}", cancellationToken) ?? [];
            airing.AddRange(schedule.Select(i => i.Show).OfType<TvMazeShow>());
        }

        var web = await http.GetFromJsonAsync<List<TvMazeScheduleItem>>("schedule/web", cancellationToken) ?? [];
        airing.AddRange(web.Select(i => i.Embedded?.Show).OfType<TvMazeShow>());

        return airing
            .Where(s => s.Type is null || !_options.ExcludedFeaturedTypes.Contains(s.Type, StringComparer.OrdinalIgnoreCase))
            .Where(s => _options.ExcludeFeaturedAiringDaysPerWeek <= 0 || (s.Schedule?.Days.Count ?? 0) < _options.ExcludeFeaturedAiringDaysPerWeek)
            .DistinctBy(s => s.Id)
            .OrderByDescending(s => s.Weight)
            .Take(_options.FeaturedCount)
            .Select(s => s.Id.ToString())
            .ToList();
    }

    /// <summary>
    /// TVmaze has no "most popular" list, so this reads its whole show index (about 380 pages of 250, ordered by id;
    /// a few minutes under the rate limit) and ranks by weight, TVmaze's popularity score, then rating.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPopularShowIdsAsync(int count, Action<int>? onPage = null, CancellationToken cancellationToken = default)
    {
        var shows = new List<TvMazeShow>();
        for (var page = 0; ; page++)
        {
            using var response = await http.GetAsync($"shows?page={page}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                break; // Past the last page.
            }

            response.EnsureSuccessStatusCode();
            var items = await response.Content.ReadFromJsonAsync<List<TvMazeShow>>(cancellationToken) ?? [];
            if (items.Count == 0)
            {
                break;
            }

            shows.AddRange(items);
            onPage?.Invoke(page + 1);
        }

        return shows
            .OrderByDescending(s => s.Weight)
            .ThenByDescending(s => s.Rating?.Average ?? 0)
            .Take(count)
            .Select(s => s.Id.ToString())
            .ToList();
    }

    public async Task<IReadOnlyList<string>> SearchShowIdsAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        // Returns up to 10 shows, best match first.
        var results = await http.GetFromJsonAsync<List<TvMazeSearchResult>>(
            $"search/shows?q={Uri.EscapeDataString(query)}", cancellationToken) ?? [];

        return results
            .Select(r => r.Show)
            .OfType<TvMazeShow>()
            .Take(limit)
            .Select(s => s.Id.ToString())
            .ToList();
    }

    public async Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default)
    {
        // In parallel with the show, so the extra call costs a request's worth of rate limit, not of time.
        var creatives = GetCreativesAsync(externalId, cancellationToken);

        using var response = await http.GetAsync(
            $"shows/{Uri.EscapeDataString(externalId)}?embed[]=episodes&embed[]=seasons&embed[]=cast&embed[]=images", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var show = await response.Content.ReadFromJsonAsync<TvMazeShow>(cancellationToken);
        if (show is null)
        {
            logger.LogWarning("TVmaze returned an empty body for show {ExternalId}", externalId);
            return null;
        }

        return Map(show) with { Creatives = await creatives };
    }

    /// <summary>
    /// The show's crew, in a call of its own: TVmaze's crew embed can fail (502) for shows whose other embeds work,
    /// and a show shouldn't fail to import over it. Null when the call fails, so the creatives are filled in later.
    /// </summary>
    private async Task<List<ProviderCreative>?> GetCreativesAsync(string externalId, CancellationToken cancellationToken)
    {
        try
        {
            var crew = await http.GetFromJsonAsync<List<TvMazeCrewCredit>>($"shows/{Uri.EscapeDataString(externalId)}/crew", cancellationToken) ?? [];
            return Creatives(crew);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // No such show; GetShowAsync reports that.
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Couldn't get the crew of TVmaze show {ExternalId}; its creatives are filled in later", externalId);
            return null;
        }
    }

    private ProviderShow Map(TvMazeShow s)
    {
        var embedded = s.Embedded ?? new TvMazeEmbedded();

        var episodes = embedded.Episodes
            .Where(e => e.Season is not null && e.Number is not null)
            .Select(e => new ProviderEpisode(
                ExternalId: e.Id.ToString(),
                SeasonNumber: e.Season!.Value,
                EpisodeNumber: e.Number!.Value,
                Title: Truncate(string.IsNullOrWhiteSpace(e.Name) ? $"Episode {e.Number}" : e.Name, 256),
                Overview: StripHtml(e.Summary),
                AirsAtUtc: e.Airstamp?.ToUniversalTime(),
                RuntimeMinutes: e.Runtime,
                Rating: e.Rating?.Average))
            .ToList();

        var seasons = embedded.Seasons
            .Select(season => new ProviderSeason(
                season.Number,
                Truncate(string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.Number}" : season.Name, 256),
                StripHtml(season.Summary),
                // Seasons only have a premiere date; the first episode's airstamp has the time as well.
                episodes.Where(e => e.SeasonNumber == season.Number && e.AirsAtUtc is not null).Min(e => e.AirsAtUtc)
                    ?? (season.PremiereDate is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null)))
            .ToList();

        // The same actor can play several characters; the catalog keys cast by person name.
        var cast = embedded.Cast
            .Where(c => !string.IsNullOrWhiteSpace(c.Person?.Name))
            .GroupBy(c => c.Person!.Name!)
            .Take(_options.MaxCastMembers)
            .Select(g => new ProviderCastMember(
                Truncate(g.Key, 256),
                Truncate(string.Join(" / ", g.Select(c => c.Character?.Name).OfType<string>().Distinct()), 256) is { Length: > 0 } character ? character : null,
                g.First().Person!.Image?.Medium,
                g.Select(c => c.Character?.Id).OfType<int>().Distinct().Select(id => id.ToString()).ToList(),
                g.First().Person is { Id: { } personId } p
                    ? new ProviderPerson(personId.ToString(), p.Birthday, p.Deathday, p.Gender, p.Country?.Name)
                    : null))
            .ToList();

        var backdrop = embedded.Images
            .Where(i => i.Type == "background")
            .OrderByDescending(i => i.Main)
            .Select(i => i.Resolutions?.Original?.Url)
            .FirstOrDefault(url => url is not null);

        return new ProviderShow(
            ExternalId: s.Id.ToString(),
            Title: Truncate(s.Name ?? $"Show {s.Id}", 256),
            Overview: StripHtml(s.Summary),
            PosterUrl: s.Image?.Original ?? s.Image?.Medium,
            BackdropUrl: backdrop,
            FirstAirDate: s.Premiered,
            Status: MapStatus(s),
            Network: s.Network?.Name ?? s.WebChannel?.Name,
            Genres: s.Genres,
            // TVmaze has no keywords; its show type ("Scripted", "Animation", "Reality", ...) and language are the closest signal.
            Keywords: new[] { s.Type, s.Language }.OfType<string>().Select(k => k.ToLowerInvariant()).ToList(),
            Popularity: s.Weight,
            Seasons: seasons,
            Episodes: episodes,
            Cast: cast,
            WatchOptions: WatchOptions(s),
            SimilarExternalIds: [],
            Rating: s.Rating?.Average);
    }

    /// <summary>
    /// TVmaze statuses are Running, Ended, To Be Determined and In Development. It doesn't distinguish
    /// cancelled from ended, or say when a show is renewed, so those catalog statuses are never used.
    /// </summary>
    private ShowStatus MapStatus(TvMazeShow s) => s.Status switch
    {
        "Ended" => ShowStatus.Ended,
        "In Development" => ShowStatus.Upcoming,
        _ when s.Premiered is null || s.Premiered > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime) => ShowStatus.Upcoming,
        // Running, or To Be Determined (between seasons with no decision yet).
        _ => ShowStatus.Airing
    };

    /// <summary>Only streaming services count as "where to watch"; a broadcast network is shown as the show's network.</summary>
    private List<ProviderWatchOption> WatchOptions(TvMazeShow s)
    {
        if (s.WebChannel?.Name is not { } name)
        {
            return [];
        }

        var regions = s.WebChannel.Country?.Code is { Length: 2 } code ? [code.ToUpperInvariant()] : _options.WatchRegions;
        return regions
            .Select(region => new ProviderWatchOption(region, Truncate(name, 128), WatchProviderType.Stream, s.WebChannel.OfficialSite ?? s.OfficialSite))
            .ToList();
    }

    /// <summary>The roles that say who made a show; TVmaze also lists writers, composers, casting and the like.</summary>
    internal static readonly string[] CreativeRoles = ["Creator", "Developer", "Executive Producer"];

    /// <summary>Creators and developers first, then executive producers, one entry per person.</summary>
    private static List<ProviderCreative> Creatives(List<TvMazeCrewCredit> crew) => crew
        .Where(c => c.Person?.Name is not null && c.Type is not null && CreativeRoles.Contains(c.Type))
        .GroupBy(c => c.Person!.Id?.ToString() ?? c.Person.Name!)
        .Select(g => new ProviderCreative(
            Truncate(g.First().Person!.Name!, 256),
            g.First().Person!.Image?.Medium,
            g.Select(c => c.Type!).Distinct().OrderBy(t => Array.IndexOf(CreativeRoles, t)).ToList(),
            g.First().Person is { Id: { } personId } p
                ? new ProviderPerson(personId.ToString(), p.Birthday, p.Deathday, p.Gender, p.Country?.Name)
                : null))
        .OrderBy(c => c.Roles.Contains("Executive Producer") && c.Roles.Count == 1)
        .ToList();

    /// <summary>One call: the person's crew credits with each show embedded (for its name and popularity).</summary>
    public async Task<ProviderCareer?> GetCareerAsync(string personExternalId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"people/{Uri.EscapeDataString(personExternalId)}/crewcredits?embed=show", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var credits = await response.Content.ReadFromJsonAsync<List<TvMazeCrewCareerCredit>>(cancellationToken) ?? [];
        var shows = credits.Where(c => c.Embedded?.Show is not null).ToList();
        if (shows.Count == 0)
        {
            return null;
        }

        int Count(params string[] types) => shows.Where(c => types.Contains(c.Type)).Select(c => c.Embedded!.Show!.Id).Distinct().Count();
        return new ProviderCareer(
            shows.Select(c => c.Embedded!.Show!.Id).Distinct().Count(),
            Count("Creator", "Developer"),
            Count("Executive Producer"),
            shows.Where(c => c.Type is not null && CreativeRoles.Contains(c.Type))
                .Select(c => c.Embedded!.Show!)
                .DistinctBy(s => s.Id)
                .OrderByDescending(s => s.Weight)
                .ThenByDescending(s => s.Rating?.Average ?? 0)
                .Take(10)
                .Select(s => Truncate(s.Name ?? $"Show {s.Id}", 256))
                .ToList());
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(HtmlTag().Replace(html, " "));
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Spaces TVmaze calls to stay under its limit of 20 calls every 10 seconds.</summary>
public sealed class TvMazeRateLimitHandler(TvMazeRateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await limiter.Limiter.AcquireAsync(1, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Singleton holder so every handler instance shares one budget.</summary>
public sealed class TvMazeRateLimiter : IDisposable
{
    public RateLimiter Limiter { get; } = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = 18,
        Window = TimeSpan.FromSeconds(10),
        SegmentsPerWindow = 5,
        QueueLimit = int.MaxValue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true
    });

    public void Dispose() => Limiter.Dispose();
}

// TVmaze response shapes (only the fields the catalog uses).

internal sealed record TvMazeScheduleItem
{
    /// <summary>Set on the country schedule.</summary>
    public TvMazeShow? Show { get; init; }

    /// <summary>Set on the web (streaming) schedule.</summary>
    [JsonPropertyName("_embedded")]
    public TvMazeScheduleEmbedded? Embedded { get; init; }
}

internal sealed record TvMazeScheduleEmbedded(TvMazeShow? Show);

internal sealed record TvMazeSearchResult(double Score, TvMazeShow? Show);

internal sealed record TvMazeShow
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Language { get; init; }
    public List<string> Genres { get; init; } = [];
    public string? Status { get; init; }
    public DateOnly? Premiered { get; init; }
    public string? OfficialSite { get; init; }
    public double Weight { get; init; }
    public TvMazeChannel? Network { get; init; }
    public TvMazeChannel? WebChannel { get; init; }
    public TvMazeImage? Image { get; init; }
    public string? Summary { get; init; }
    public TvMazeRating? Rating { get; init; }
    public TvMazeSchedule? Schedule { get; init; }

    [JsonPropertyName("_embedded")]
    public TvMazeEmbedded? Embedded { get; init; }
}

internal sealed record TvMazeSchedule
{
    public List<string> Days { get; init; } = [];
}

internal sealed record TvMazeChannel(string? Name, TvMazeCountry? Country, string? OfficialSite);

internal sealed record TvMazeCountry(string? Code, string? Name);

internal sealed record TvMazeImage(string? Medium, string? Original);

internal sealed record TvMazeEmbedded
{
    public List<TvMazeEpisode> Episodes { get; init; } = [];
    public List<TvMazeSeason> Seasons { get; init; } = [];
    public List<TvMazeCastCredit> Cast { get; init; } = [];
    public List<TvMazeShowImage> Images { get; init; } = [];
}

internal sealed record TvMazeEpisode(int Id, string? Name, int? Season, int? Number, DateTimeOffset? Airstamp, int? Runtime, string? Summary, TvMazeRating? Rating);

internal sealed record TvMazeSeason(int Number, string? Name, DateOnly? PremiereDate, string? Summary);

internal sealed record TvMazeCastCredit(TvMazePerson? Person, TvMazeCharacter? Character);

internal sealed record TvMazeCrewCredit(string? Type, TvMazePerson? Person);

/// <summary>An entry of /people/{id}/crewcredits?embed=show.</summary>
internal sealed record TvMazeCrewCareerCredit
{
    public string? Type { get; init; }

    [JsonPropertyName("_embedded")]
    public TvMazeCrewCareerEmbedded? Embedded { get; init; }
}

internal sealed record TvMazeCrewCareerEmbedded(TvMazeShow? Show);

internal sealed record TvMazePerson(int? Id, string? Name, TvMazeImage? Image, DateOnly? Birthday, DateOnly? Deathday, string? Gender, TvMazeCountry? Country);

internal sealed record TvMazeCharacter(int? Id, string? Name);

internal sealed record TvMazeRating(double? Average);

internal sealed record TvMazeShowImage(string? Type, bool Main, TvMazeImageResolutions? Resolutions);

internal sealed record TvMazeImageResolutions(TvMazeImageUrl? Original);

internal sealed record TvMazeImageUrl(string? Url);

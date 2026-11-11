using System.Globalization;
using Catalog.Data;
using Microsoft.Extensions.Options;
using static Catalog.SyncWorker.Providers.Tvdb.TvdbMapping;

namespace Catalog.SyncWorker.Providers.Tvdb;

/// <summary>
/// The catalog from TheTVDB's v4 API (<c>Catalog:Provider=tvdb</c>), which needs an API key (<see cref="TvdbOptions"/>)
/// and, by its licence, a credit to TheTVDB wherever its data shows (the app's footer carries it).
/// <para>
/// What it has that the others lack: a show's ids are TheTVDB's own, so a TV Time history imports without a lookup.
/// Titles, overviews and episode names are read in <see cref="TvdbOptions.Language"/> where TheTVDB has them, and
/// episodes in aired order. What it doesn't have: ratings, where to watch and similar shows; an episode's air time is
/// its day at the series' time (<see cref="TvdbMapping.AirsAt"/>); people come without birthdays, and a person's acting credits elsewhere aren't read (their record doesn't say enough about each
/// show), so the features built on those stay empty for TheTVDB's shows.
/// </para>
/// </summary>
public sealed class TvdbCatalogProvider(TvdbClient tvdb, IOptions<TvdbOptions> options, TimeProvider time, ILogger<TvdbCatalogProvider> logger)
    : ICatalogProvider
{
    private const int SearchLimit = 20;

    /// <summary>
    /// Pages of 500 episodes: more than the longest-running daily shows have.
    /// </summary>
    private const int MaxEpisodePages = 40;

    /// <summary>
    /// Pages of the update feed read for one answer. The feed is everything TheTVDB changed, not only our shows, so a
    /// week of it (asked for when the worker starts) runs long; past this many pages what was read is used as it is.
    /// </summary>
    private const int MaxUpdatePages = 100;

    private static readonly TimeSpan FeaturedFor = TimeSpan.FromDays(1);

    private (DateTimeOffset At, IReadOnlyList<string> Ids)? featured;

    public string Name => CatalogSources.Tvdb;

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The best-scored shows of the configured countries, read once a day.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetFeaturedShowIdsAsync(CancellationToken cancellationToken = default)
    {
        if (featured is { } known && time.GetUtcNow() - known.At < FeaturedFor)
        {
            return known.Ids;
        }
        var ids = await GetPopularShowIdsAsync(options.Value.FeaturedCount, cancellationToken: cancellationToken);
        featured = (time.GetUtcNow(), ids);
        return ids;
    }

    /// <summary>
    /// By TheTVDB's score, across the configured countries of origin (its filter needs a country and a language, and
    /// answers with one list per country, so the index isn't paged further than that).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPopularShowIdsAsync(int count, Action<int>? onPage = null, CancellationToken cancellationToken = default)
    {
        var language = Uri.EscapeDataString(options.Value.FeaturedLanguage);
        var shows = new List<TvdbSeries>();
        var pages = 0;
        foreach (var country in options.Value.Countries)
        {
            var found = await tvdb.GetAsync<List<TvdbSeries>>(
                $"series/filter?country={Uri.EscapeDataString(country)}&lang={language}&sort=score&sortType=desc", cancellationToken);
            shows.AddRange(found?.Data ?? []);
            onPage?.Invoke(++pages);
        }
        return shows
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .OrderByDescending(s => s.Score ?? 0)
            .ThenBy(s => s.Id)
            .Take(count)
            .Select(s => Text(s.Id))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> SearchShowIdsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        (await SearchAsync(query, limit, cancellationToken)).Select(s => s.ExternalId).ToList();

    public async Task<IReadOnlyList<ProviderShowSummary>> SearchShowsAsync(string query, CancellationToken cancellationToken = default) =>
        await SearchAsync(query, SearchLimit, cancellationToken);

    private async Task<List<ProviderShowSummary>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }
        var found = await tvdb.GetAsync<List<TvdbSearchResult>>(
            $"search?query={Uri.EscapeDataString(query.Trim())}&type=series&limit={limit}", ct);
        return (found?.Data ?? [])
            .Select(hit => Summary(hit, options.Value.Language))
            .OfType<ProviderShowSummary>()
            .DistinctBy(s => s.ExternalId)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// The id is the provider's own; checked, so a show TheTVDB has since deleted or merged away isn't promised.
    /// </summary>
    public async Task<string?> LookUpShowByTvdbAsync(string tvdbId, CancellationToken cancellationToken = default) =>
        IsId(tvdbId) && (await tvdb.GetAsync<TvdbSeries>($"series/{tvdbId}", cancellationToken))?.Data is { } series
            ? Text(series.Id)
            : null;

    public async Task<string?> LookUpShowByImdbAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }
        // An IMDb id can also be a movie's, a person's or an episode's: only a series can be imported.
        var matches = await tvdb.GetAsync<List<TvdbRemoteMatch>>($"search/remoteid/{Uri.EscapeDataString(imdbId.Trim())}", cancellationToken);
        return matches?.Data?.Select(m => m.Series).FirstOrDefault(s => s is { Id: > 0 }) is { } series ? Text(series.Id) : null;
    }

    /// <summary>
    /// From TheTVDB's update feed: shows that changed, and the shows of episodes that changed.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetChangedShowsAsync(TimeSpan within, CancellationToken cancellationToken = default)
    {
        var series = await UpdatesAsync("series", within, cancellationToken);
        var episodes = await UpdatesAsync("episodes", within, cancellationToken);
        var changed = new Dictionary<string, DateTimeOffset>();
        foreach (var (id, at) in series.Select(u => (u.RecordId, u.TimeStamp))
            .Concat(episodes.Where(u => u.SeriesId is > 0).Select(u => (u.SeriesId!.Value, u.TimeStamp))))
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(at);
            var key = Text(id);
            if (!changed.TryGetValue(key, out var known) || when > known)
            {
                changed[key] = when;
            }
        }
        return changed;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetChangedPeopleAsync(TimeSpan within, CancellationToken cancellationToken = default) =>
        (await UpdatesAsync("people", within, cancellationToken))
            .GroupBy(u => Text(u.RecordId)).ToDictionary(g => g.Key, g => DateTimeOffset.FromUnixTimeSeconds(g.Max(u => u.TimeStamp)));

    /// <summary>
    /// The records of a type created or updated within a time (deletions left out). Never null: the answer only marks
    /// shows due for a refresh sooner, so when the feed runs past <see cref="MaxUpdatePages"/> the part read is still
    /// worth having, and the shows it misses are refreshed on their timers.
    /// </summary>
    private async Task<List<TvdbUpdate>> UpdatesAsync(string type, TimeSpan within, CancellationToken ct)
    {
        var since = (time.GetUtcNow() - within).ToUnixTimeSeconds();
        var updates = new List<TvdbUpdate>();
        for (var page = 0; page < MaxUpdatePages; page++)
        {
            // No action filter: a new episode is a creation, and "update" alone would leave it out.
            var found = await tvdb.GetAsync<List<TvdbUpdate>>($"updates?since={since}&type={type}&page={page}", ct);
            updates.AddRange((found?.Data ?? []).Where(u => u.MethodInt != TvdbUpdate.Deleted));
            if (found?.Data is not { Count: > 0 } || found.Links?.Next is null)
            {
                return updates;
            }
        }
        logger.LogInformation("TheTVDB's {Type} updates since {Since} run past {Pages} pages; using the {Count} read",
            type, DateTimeOffset.FromUnixTimeSeconds(since), MaxUpdatePages, updates.Count);
        return updates;
    }

    public async Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default)
    {
        var language = options.Value.Language;
        if (!IsId(externalId)
            || (await tvdb.GetAsync<TvdbSeries>($"series/{externalId}/extended?meta=translations", cancellationToken))?.Data is not { } series)
        {
            return null;
        }

        var episodes = await EpisodesAsync(externalId, null, cancellationToken);
        // A show made in another language: its episodes again in ours, for the names and overviews TheTVDB has.
        if (!string.Equals(series.OriginalLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            episodes = WithTranslations(episodes, await EpisodesAsync(externalId, language, cancellationToken)).ToList();
        }
        return Show(series, episodes, Today, language);
    }

    /// <summary>
    /// A show's episodes in aired order ("official"), page by page, in a language when one is given. Aired order, not
    /// the series' default (which can be DVD or absolute): it's the order the seasons' names belong to and the one a
    /// TV Time history numbers episodes by. A series with nothing in aired order is read in its default.
    /// </summary>
    private async Task<List<TvdbEpisode>> EpisodesAsync(string externalId, string? language, CancellationToken ct)
    {
        foreach (var order in new[] { "official", "default" })
        {
            var episodes = new List<TvdbEpisode>();
            for (var page = 0; page < MaxEpisodePages; page++)
            {
                var found = await tvdb.GetAsync<TvdbEpisodePage>(
                    $"series/{externalId}/episodes/{order}{(language is null ? "" : "/" + Uri.EscapeDataString(language))}?page={page}", ct);
                var read = found?.Data?.All ?? [];
                episodes.AddRange(read);
                if (read.Count == 0 || found?.Links?.Next is null)
                {
                    break;
                }
            }
            if (episodes.Count > 0)
            {
                return episodes;
            }
        }
        return [];
    }

    /// <summary>
    /// How many shows they created or executive-produced, and the first ten as TheTVDB lists them. The shows they made
    /// aren't given as shows (<see cref="ProviderCareer.Made"/> stays null): a person's record names each series
    /// without its dates, status or score.
    /// </summary>
    public async Task<ProviderCareer?> GetCareerAsync(string personExternalId, CancellationToken cancellationToken = default)
    {
        if (!IsId(personExternalId)
            || (await tvdb.GetAsync<TvdbPerson>($"people/{personExternalId}/extended", cancellationToken))?.Data is not { } person)
        {
            return null;
        }
        var shows = (person.Characters ?? [])
            .Where(c => c.SeriesId is > 0 && CreativeRole(c.PeopleType) is not null)
            .GroupBy(c => c.SeriesId!.Value)
            .Select(g => (Title: g.Select(c => c.Series?.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                Roles: g.Select(c => CreativeRole(c.PeopleType)!).ToHashSet()))
            .ToList();
        return shows.Count == 0
            ? null
            : new ProviderCareer(
                shows.Count,
                shows.Count(s => s.Roles.Contains("Creator")),
                shows.Count(s => s.Roles.Contains("Executive Producer")),
                shows.Select(s => s.Title).OfType<string>().Take(10).ToList());
    }

    /// <summary>
    /// Not read from TheTVDB: a person's record names each series they acted in without its status, dates or score,
    /// which a role needs. Null, so nobody's credits are replaced with half of them.
    /// </summary>
    public Task<IReadOnlyList<ProviderActingRole>?> GetCastCreditsAsync(string personExternalId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderActingRole>?>(null);

    private static string Text(long id) => id.ToString(CultureInfo.InvariantCulture);
}

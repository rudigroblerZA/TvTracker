using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Research.API.Apis;
using Research.API.Data;
using TvTracker;

namespace Research.API.Services;

/// <summary>
/// Why saved research is due for a refresh, as the research queue lists it.
/// </summary>
public static class RefreshReasons
{
    /// <summary>
    /// The catalog has more regular seasons than when the research was saved.
    /// </summary>
    public const string NewSeason = "new-season";

    /// <summary>
    /// Wikidata disagrees with the catalog about a field the saved research didn't settle (the daily <see cref="CatalogChecks"/>).
    /// </summary>
    public const string Wikidata = "wikidata";

    /// <summary>
    /// Saved more than <see cref="MaxAge"/> ago.
    /// </summary>
    public const string Old = "old";

    /// <summary>
    /// Not a refresh: a popular show with no research at all, as the assistant records it.
    /// </summary>
    public const string Unresearched = "unresearched";

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    /// <summary>
    /// Regular seasons with episodes, as the catalog lists them: what <see cref="SavedResearch.SeasonsAtSave"/> keeps.
    /// </summary>
    public static int Seasons(CatalogShow show) => show.Seasons.Count(s => s.Number > 0 && s.EpisodeCount > 0);

    /// <summary>
    /// Whether a Wikidata disagreement calls for a refresh: only a check made after the save (the table is replaced
    /// daily, so a fix saved today still shows yesterday's row), and only for a field the saved research didn't settle.
    /// A moderator who saved a network or premiere date has judged Wikidata's, even when they kept the catalog's
    /// (a streaming home against a first broadcaster), so it isn't raised again after every save.
    /// </summary>
    public static bool WikidataDisagrees(IEnumerable<(string Field, DateTime CheckedAtUtc)> checks, DateTime savedAtUtc, IReadOnlySet<string> savedFacts) =>
        checks.Any(c => c.CheckedAtUtc > savedAtUtc && !savedFacts.Contains(c.Field));

    /// <summary>
    /// The reasons saved research needs a refresh, empty when it doesn't. A version saved before seasons were kept
    /// (<paramref name="seasonsAtSave"/> null), or a show whose seasons couldn't be read, is judged by age and Wikidata only.
    /// </summary>
    public static List<string> For(DateTime savedAtUtc, int? seasonsAtSave, int? seasonsNow, bool wikidataDisagrees, DateTime nowUtc)
    {
        var reasons = new List<string>();
        if (seasonsAtSave is { } then && seasonsNow is { } now && now > then)
        {
            reasons.Add(NewSeason);
        }
        if (wikidataDisagrees)
        {
            reasons.Add(Wikidata);
        }
        if (nowUtc - savedAtUtc > MaxAge)
        {
            reasons.Add(Old);
        }
        return reasons;
    }
}

/// <param name="Rank">Its place among the catalog's most popular.</param>
/// <param name="Reasons"><see cref="RefreshReasons"/>, in that order.</param>
/// <param name="Prepared">The assistant has gathered and drafted for it since the last save, for a moderator to review.</param>
public record RefreshShowDto(Guid ShowId, string Title, int Rank, int Version, DateTime SavedAtUtc, IReadOnlyList<string> Reasons, bool Prepared);

/// <summary>
/// Saved research among the catalog's most popular shows that's due for a refresh, for the queue and the assistant.
/// </summary>
public static class RefreshQueue
{
    /// <summary>
    /// Shows looked up at once for their current seasons.
    /// </summary>
    private const int Parallel = 8;

    /// <summary>
    /// A show's season count is kept this long, so reading the queue doesn't ask Catalog.API about every researched show
    /// each time; a new season shows up within the hour.
    /// </summary>
    private static readonly TimeSpan SeasonsFor = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<List<RefreshShowDto>> ComputeAsync(ResearchContext db, CatalogReader catalog, IMemoryCache cache,
        IReadOnlyList<CatalogShowFacts> popular, DateTime nowUtc, ILogger logger, CancellationToken ct)
    {
        var ranks = popular.Select((s, i) => (s.Id, Rank: i + 1)).ToDictionary(x => x.Id, x => x.Rank);
        var ids = ranks.Keys.ToList();
        var latest = await db.Saved.AsNoTracking().Where(s => ids.Contains(s.ShowId))
            .GroupBy(s => s.ShowId)
            .Select(g => g.OrderByDescending(s => s.Version)
                .Select(s => new { s.ShowId, s.Version, s.SavedAtUtc, s.SeasonsAtSave, s.Facts }).First())
            .ToListAsync(ct);
        if (latest.Count == 0)
        {
            return [];
        }

        var checks = (await db.CatalogChecks.AsNoTracking().Select(c => new { c.ShowId, c.Field, c.CheckedAtUtc }).ToListAsync(ct))
            .ToLookup(c => c.ShowId, c => (c.Field, c.CheckedAtUtc));
        var prepared = await db.Dossiers.AsNoTracking().Where(d => ids.Contains(d.ShowId) && d.AssistantPreparedAtUtc != null)
            .Select(d => new { d.ShowId, d.AssistantPreparedAtUtc })
            .ToDictionaryAsync(d => d.ShowId, d => d.AssistantPreparedAtUtc!.Value, ct);
        var titles = popular.ToDictionary(s => s.Id, s => s.Title);

        var seasons = new Dictionary<Guid, int?>();
        await System.Threading.Tasks.Parallel.ForEachAsync(latest.Where(s => s.SeasonsAtSave is not null),
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (saved, token) =>
            {
                var count = await SeasonsAsync(catalog, cache, saved.ShowId, logger, token);
                lock (seasons)
                {
                    seasons[saved.ShowId] = count;
                }
            });

        return latest
            .Select(s =>
            {
                var facts = (JsonSerializer.Deserialize<List<SavedFact>>(s.Facts, Json) ?? []).Select(f => f.Key).ToHashSet();
                var wikidata = RefreshReasons.WikidataDisagrees(checks[s.ShowId], s.SavedAtUtc, facts);
                return (Saved: s, Reasons: RefreshReasons.For(s.SavedAtUtc, s.SeasonsAtSave, seasons.GetValueOrDefault(s.ShowId), wikidata, nowUtc));
            })
            .Where(x => x.Reasons.Count > 0)
            .OrderBy(x => ranks[x.Saved.ShowId])
            .Select(x => new RefreshShowDto(x.Saved.ShowId, titles[x.Saved.ShowId], ranks[x.Saved.ShowId], x.Saved.Version, x.Saved.SavedAtUtc,
                x.Reasons, prepared.TryGetValue(x.Saved.ShowId, out var at) && at > x.Saved.SavedAtUtc))
            .ToList();
    }

    /// <summary>
    /// The show's regular seasons now, cached; null when Catalog.API couldn't say, so one show it can't read doesn't fail
    /// the queue (the show just isn't judged on seasons this time).
    /// </summary>
    private static async Task<int?> SeasonsAsync(CatalogReader catalog, IMemoryCache cache, Guid showId, ILogger logger, CancellationToken ct)
    {
        var key = (nameof(RefreshQueue), showId);
        if (cache.TryGetValue(key, out int known))
        {
            return known;
        }
        try
        {
            if (await catalog.GetShowAsync(showId, ct) is not { } show)
            {
                return null;
            }
            var count = RefreshReasons.Seasons(show);
            cache.Set(key, count, SeasonsFor);
            return count;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't read {ShowId}'s seasons for the research queue", showId);
            return null;
        }
    }
}

/// <summary>
/// The research assistant: a few times a day it takes the top of the research queue and does the legwork a moderator
/// would start with, gathering the sources and queueing a draft (<see cref="DraftQueue"/>), so the moderator opens a
/// prepared workspace. It never saves: the saved record, and the catalog fields it overrides, stay a moderator's call.
/// <list type="bullet">
/// <item><b>Whose change is whose:</b> a workspace's <c>UpdatedAtUtc</c> moves only for a moderator's change (gather,
/// sources, a draft asked for or edited, a save) or when the assistant finishes preparing it, which stamps the same moment
/// as <c>AssistantPreparedAtUtc</c>. The assistant's own gather and a draft finishing in the background don't move it, so
/// an attempt that fails part way leaves the workspace as it was and is tried again next run.</item>
/// <item><b>What it picks,</b> most popular first: saved research due a refresh (<see cref="RefreshQueue"/>) whose
/// workspace hasn't changed since the save, then popular shows with no research and no workspace anyone has worked on.
/// A show it prepared isn't picked again until it's saved.</item>
/// <item><b>Never over a moderator's work:</b> just before preparing a show it reads the workspace again, and leaves it if
/// anyone changed it since the save or a draft is running.</item>
/// <item><b>Gently:</b> <see cref="PerRun"/> shows a run, <see cref="BetweenShows"/> apart whatever happened to the last
/// one, since Wikidata limits the sandbox's shared address hard and drafts take minutes each on a CPU; one show failing
/// doesn't stop the run.</item>
/// <item><b>Behind <c>research-assistant</c>,</b> checked once a run without a user. Moderators can start a run at once
/// (<c>POST /api/v1/research/assistant/run</c>).</item>
/// </list>
/// </summary>
public sealed class ResearchAssistant(IServiceScopeFactory scopes, DraftQueue drafts, TimeProvider time, ILogger<ResearchAssistant> logger)
    : BackgroundService
{
    public const int PerRun = 3;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan BetweenShows = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim wake = new(0, 1);

    /// <summary>
    /// Starts a run now rather than at the next interval; a run already waiting to start is not doubled.
    /// </summary>
    public void RunNow()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A run is already due to start.
        }
    }

    /// <summary>
    /// No workspace, or one no one has changed since the research was saved (the save stamps it); with nothing saved,
    /// one no one has changed at all (<see cref="DateTime.UnixEpoch"/>).
    /// </summary>
    internal static bool Untouched(Dossier? dossier, DateTime savedAtUtc) => dossier is null || dossier.UpdatedAtUtc <= savedAtUtc;

    /// <summary>
    /// The assistant's preparation is the last change to the workspace, so what's there is its work.
    /// </summary>
    internal static bool PreparedLast(Dossier dossier) => dossier.AssistantPreparedAtUtc is { } at && dossier.UpdatedAtUtc == at;

    private static bool Drafting(Dossier? dossier, DateTime nowUtc) =>
        dossier?.DraftingSinceUtc is { } since && nowUtc - since < DraftQueue.Stale;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await wake.WaitAsync(FirstDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The research assistant's run failed; it tries again in {Hours} hours", Interval.TotalHours);
            }
            await wake.WaitAsync(Interval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        List<(Guid ShowId, string Reason)> picks;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            if (!await services.GetRequiredService<CachedFlags>().IsEnabledAsync(FeatureFlags.ResearchAssistant, FeatureFlags.ContextFor(null), ct))
            {
                return;
            }
            var db = services.GetRequiredService<ResearchContext>();
            var catalog = services.GetRequiredService<CatalogReader>();
            var now = time.GetUtcNow().UtcDateTime;

            var popular = await catalog.GetPopularFactsAsync(CatalogChecks.Popular, ct);
            var refresh = await RefreshQueue.ComputeAsync(db, catalog, services.GetRequiredService<IMemoryCache>(), popular, now, logger, ct);
            var dossiers = await db.Dossiers.AsNoTracking()
                .Select(d => new Dossier { ShowId = d.ShowId, ShowTitle = d.ShowTitle, UpdatedAtUtc = d.UpdatedAtUtc, DraftingSinceUtc = d.DraftingSinceUtc })
                .ToDictionaryAsync(d => d.ShowId, ct);
            var saved = (await db.Saved.Select(s => s.ShowId).Distinct().ToListAsync(ct)).ToHashSet();

            bool Free(Guid showId, DateTime savedAtUtc) =>
                Untouched(dossiers.GetValueOrDefault(showId), savedAtUtc) && !Drafting(dossiers.GetValueOrDefault(showId), now);

            picks = refresh
                .Where(r => !r.Prepared && Free(r.ShowId, r.SavedAtUtc))
                .Select(r => (r.ShowId, Reason: r.Reasons[0]))
                .Concat(popular.Where(s => !saved.Contains(s.Id) && Free(s.Id, DateTime.UnixEpoch))
                    .Select(s => (ShowId: s.Id, Reason: RefreshReasons.Unresearched)))
                .Take(PerRun)
                .ToList();
        }

        var prepared = 0;
        for (var i = 0; i < picks.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(BetweenShows, time, ct);
            }
            var (showId, reason) = picks[i];
            try
            {
                // A scope per show, so a failed save can't leave changes behind for the next one.
                await using var scope = scopes.CreateAsyncScope();
                if (await PrepareAsync(scope.ServiceProvider, showId, reason, ct))
                {
                    prepared++;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The research assistant couldn't prepare {ShowId}; it's tried again next run", showId);
            }
        }
        if (picks.Count > 0)
        {
            logger.LogInformation("The research assistant prepared {Prepared} of {Picked} shows for moderators", prepared, picks.Count);
        }
    }

    /// <summary>
    /// Gathers the show's sources and queues a draft from them. False when the show is gone, someone has started on it
    /// since the run began, or nothing could be read (then the workspace says so, and counts as prepared).
    /// </summary>
    private async Task<bool> PrepareAsync(IServiceProvider services, Guid showId, string reason, CancellationToken ct)
    {
        var db = services.GetRequiredService<ResearchContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var savedAt = await db.Saved.Where(s => s.ShowId == showId).MaxAsync(s => (DateTime?)s.SavedAtUtc, ct) ?? DateTime.UnixEpoch;
        var current = await db.Dossiers.AsNoTracking().SingleOrDefaultAsync(d => d.ShowId == showId, ct);
        if (!Untouched(current, savedAt) || Drafting(current, now)
            || await services.GetRequiredService<CatalogReader>().GetShowAsync(showId, ct) is not { } show)
        {
            return false;
        }

        var notes = await ResearchApi.GatherSourcesAsync(db, show, services.GetRequiredService<WikimediaSource>(),
            services.GetRequiredService<TmdbSource>(), now, ct, byModerator: false);
        var numbered = ResearchApi.Number(await db.Sources.AsNoTracking().Where(s => s.ShowId == showId).ToListAsync(ct));
        var dossier = await ResearchApi.EnsureDossierAsync(db, show, now, ct, touch: false);
        dossier.AssistantPreparedAtUtc = now;
        dossier.AssistantReason = reason;
        dossier.UpdatedAtUtc = now;
        if (numbered.Count == 0)
        {
            dossier.DraftProblem = $"The research assistant found no sources: {string.Join(" ", notes)}";
            await db.SaveChangesAsync(ct);
            return false;
        }

        dossier.DraftingSinceUtc = now;
        dossier.DraftProblem = null;
        await db.SaveChangesAsync(ct);
        drafts.Enqueue(showId, dossier.ShowTitle, numbered);
        return true;
    }
}

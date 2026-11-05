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
    /// Wikidata now disagrees with the catalog about the show (the daily <see cref="CatalogChecks"/>).
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
    /// The reasons saved research needs a refresh, empty when it doesn't. A version saved before seasons were kept
    /// (<paramref name="seasonsAtSave"/> null) is judged by age and Wikidata only.
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
    /// Shows looked up at once for their current seasons; Catalog.API caches show detail for a minute.
    /// </summary>
    private const int Parallel = 8;

    public static async Task<List<RefreshShowDto>> ComputeAsync(ResearchContext db, CatalogReader catalog, IReadOnlyList<CatalogShowFacts> popular,
        DateTime nowUtc, CancellationToken ct)
    {
        var ranks = popular.Select((s, i) => (s.Id, Rank: i + 1)).ToDictionary(x => x.Id, x => x.Rank);
        var ids = ranks.Keys.ToList();
        var latest = await db.Saved.AsNoTracking().Where(s => ids.Contains(s.ShowId))
            .GroupBy(s => s.ShowId)
            .Select(g => g.OrderByDescending(s => s.Version).Select(s => new { s.ShowId, s.Version, s.SavedAtUtc, s.SeasonsAtSave }).First())
            .ToListAsync(ct);
        if (latest.Count == 0)
        {
            return [];
        }

        var disagreeing = (await db.CatalogChecks.AsNoTracking().Select(c => c.ShowId).Distinct().ToListAsync(ct)).ToHashSet();
        var dossiers = await db.Dossiers.AsNoTracking().Where(d => ids.Contains(d.ShowId))
            .ToDictionaryAsync(d => d.ShowId, d => d.AssistantPreparedAtUtc, ct);
        var titles = popular.ToDictionary(s => s.Id, s => s.Title);

        var seasons = new Dictionary<Guid, int?>();
        await System.Threading.Tasks.Parallel.ForEachAsync(latest.Where(s => s.SeasonsAtSave is not null),
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (saved, token) =>
            {
                var show = await catalog.GetShowAsync(saved.ShowId, token);
                lock (seasons)
                {
                    seasons[saved.ShowId] = show is null ? null : RefreshReasons.Seasons(show);
                }
            });

        return latest
            .Select(s => (Saved: s, Reasons: RefreshReasons.For(s.SavedAtUtc, s.SeasonsAtSave, seasons.GetValueOrDefault(s.ShowId),
                disagreeing.Contains(s.ShowId), nowUtc)))
            .Where(x => x.Reasons.Count > 0)
            .OrderBy(x => ranks[x.Saved.ShowId])
            .Select(x => new RefreshShowDto(x.Saved.ShowId, titles[x.Saved.ShowId], ranks[x.Saved.ShowId], x.Saved.Version, x.Saved.SavedAtUtc,
                x.Reasons, dossiers.GetValueOrDefault(x.Saved.ShowId) is { } prepared && prepared > x.Saved.SavedAtUtc))
            .ToList();
    }
}

/// <summary>
/// The research assistant: a few times a day it takes the top of the research queue and does the legwork a moderator
/// would start with, gathering the sources and queueing a draft (<see cref="DraftQueue"/>), so the moderator opens a
/// prepared workspace. It never saves: the saved record, and the catalog fields it overrides, stay a moderator's call.
/// <list type="bullet">
/// <item><b>What it picks,</b> most popular first: saved research due for a refresh (<see cref="RefreshQueue"/>) whose
/// workspace no one has touched since the save, then popular shows with no research and no workspace at all. A show it
/// already prepared isn't picked again until it's saved (or, unresearched, ever: a moderator takes it from there).</item>
/// <item><b>Never over a moderator's work:</b> a workspace changed since its last save (gathered, edited, drafted) is
/// someone's work in progress, and the assistant leaves it alone.</item>
/// <item><b>Gently:</b> <see cref="PerRun"/> shows a run, <see cref="BetweenShows"/> apart, since Wikidata limits the
/// sandbox's shared address hard and drafts take minutes each on a CPU; one show failing doesn't stop the run.</item>
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
        if (wake.CurrentCount == 0)
        {
            wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await wake.WaitAsync(FirstDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The research assistant's run failed; it tries again in {Hours} hours", Interval.TotalHours);
            }
            await wake.WaitAsync(Interval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (!await services.GetRequiredService<CachedFlags>().IsEnabledAsync(FeatureFlags.ResearchAssistant, FeatureFlags.ContextFor(null), ct))
        {
            return;
        }
        var db = services.GetRequiredService<ResearchContext>();
        var catalog = services.GetRequiredService<CatalogReader>();
        var now = time.GetUtcNow().UtcDateTime;

        var popular = await catalog.GetPopularFactsAsync(CatalogChecks.Popular, ct);
        var refresh = await RefreshQueue.ComputeAsync(db, catalog, popular, now, ct);
        var dossiers = await db.Dossiers.AsNoTracking().ToDictionaryAsync(d => d.ShowId, ct);
        var saved = (await db.Saved.Select(s => s.ShowId).Distinct().ToListAsync(ct)).ToHashSet();

        var picks = refresh
            .Where(r => !r.Prepared && Untouched(dossiers.GetValueOrDefault(r.ShowId), r.SavedAtUtc))
            .Select(r => (r.ShowId, Reason: r.Reasons[0]))
            .Concat(popular.Where(s => !saved.Contains(s.Id) && !dossiers.ContainsKey(s.Id)).Select(s => (ShowId: s.Id, Reason: RefreshReasons.Unresearched)))
            .Take(PerRun)
            .ToList();

        var prepared = 0;
        foreach (var (showId, reason) in picks)
        {
            if (prepared > 0)
            {
                await Task.Delay(BetweenShows, time, ct);
            }
            try
            {
                if (await PrepareAsync(services, showId, reason, ct))
                {
                    prepared++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// No workspace, or one no moderator has changed since the research was saved (the save stamps it).
    /// </summary>
    internal static bool Untouched(Dossier? dossier, DateTime savedAtUtc) => dossier is null || dossier.UpdatedAtUtc <= savedAtUtc;

    /// <summary>
    /// Gathers the show's sources and queues a draft from them; false when the show is gone or nothing could be read.
    /// </summary>
    private async Task<bool> PrepareAsync(IServiceProvider services, Guid showId, string reason, CancellationToken ct)
    {
        var db = services.GetRequiredService<ResearchContext>();
        if (await services.GetRequiredService<CatalogReader>().GetShowAsync(showId, ct) is not { } show)
        {
            return false;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var notes = await ResearchApi.GatherSourcesAsync(db, show, services.GetRequiredService<WikimediaSource>(),
            services.GetRequiredService<TmdbSource>(), now, ct);
        var numbered = ResearchApi.Number(await db.Sources.AsNoTracking().Where(s => s.ShowId == showId).ToListAsync(ct));
        var dossier = await ResearchApi.EnsureDossierAsync(db, show, now, ct);
        dossier.AssistantPreparedAtUtc = now;
        dossier.AssistantReason = reason;
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

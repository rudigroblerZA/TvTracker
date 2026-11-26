using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Fits your time off (see <see cref="TimeOff"/>): the shelf of finished shows is read once an hour, the caller's
/// own watches and taste on request.
/// </summary>
public static class TimeOffApi
{
    private const int MinHours = 2;
    private const int MaxHours = 200;

    /// <summary>
    /// The finished shows and what's known about their endings are the same for everyone; kept this long.
    /// </summary>
    private static readonly HybridCacheEntryOptions ShelfCache = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };

    /// <summary>
    /// The finished shows, with only what the assessment reads. Immutable, so HybridCache hands out the cached instance
    /// rather than deserializing several hundred shows on every request.
    /// </summary>
    [System.ComponentModel.ImmutableObject(true)]
    public sealed record Shelf(IReadOnlyList<ShelfShow> Shows);

    public static void MapTimeOffApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/time-off", GetAsync).RequireFeature(FeatureFlags.TimeOff);

    private static async Task<Results<Ok<TimeOffDto>, ValidationProblem>> GetAsync(
        ClaimsPrincipal user, AnalyticsContext db, HybridCache cache, TimeProvider time, int hours = 20, CancellationToken ct = default)
    {
        var userId = user.GetUserId();
        if (hours is < MinHours or > MaxHours)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = [$"Use {MinHours} to {MaxHours} hours."] });
        }

        return TypedResults.Ok(TimeOff.Of(await CandidatesAsync(db, cache, userId, time.GetUtcNow(), ct), hours * 60));
    }

    /// <summary>
    /// The finished shows worth offering the caller, which Fits your time off and While you wait share: on the shelf, not
    /// started (<paramref name="started"/> when the caller already knows, else from the watches), at least
    /// <see cref="TimeOff.MinMinutes"/> long, assessed against the caller's liked genres, and not fizzling out at the end. Each
    /// caller applies its own length and value cut.
    /// </summary>
    internal static async Task<List<ShelfCandidate>> CandidatesAsync(AnalyticsContext db, HybridCache cache, string userId, DateTimeOffset now,
        CancellationToken ct, IReadOnlySet<Guid>? started = null)
    {
        // The shelf (every finished show, its running time and its ending) is read once an hour, not per request: it
        // used to mean loading and deserializing several hundred documents for each visit.
        var shelf = await GetShelfAsync(db, cache, now, ct);
        started ??= (await db.Watches.AsNoTracking().Where(w => w.UserId == userId).Select(w => w.ShowId).Distinct().ToListAsync(ct)).ToHashSet();
        return TimeOff.Candidates(shelf.Shows, started, await ShowValueFacts.LikedGenresAsync(db, userId, now, ct));
    }

    /// <summary>
    /// The shelf from the cache, built when it's missing; shared with While you wait.
    /// </summary>
    internal static async Task<Shelf> GetShelfAsync(AnalyticsContext db, HybridCache cache, DateTimeOffset now, CancellationToken ct) =>
        await cache.GetOrCreateAsync("time-off:shelf", async token => await ShelfAsync(db, now, token), ShelfCache, cancellationToken: ct);

    /// <summary>
    /// Every ended show with a running time, and the parts of its document the assessment reads.
    /// </summary>
    private static async Task<Shelf> ShelfAsync(AnalyticsContext db, DateTimeOffset now, CancellationToken ct)
    {
        var ended = await db.Shows.AsNoTracking().Ended().ToListAsync(ct);
        var runs = await ShowValueFacts.RunTimesAsync(db, ended.Select(s => s.ShowId).ToList(), now, ct);
        var insights = await ShowDocuments.LoadAsync(db, runs.Keys.ToList(), ct);
        return new Shelf(ended.Where(s => runs.ContainsKey(s.ShowId))
            .Select(s =>
            {
                var document = insights.GetValueOrDefault(s.ShowId);
                return new ShelfShow(s.ShowId, s.Title, s.PosterUrl, s.ExternalRating, s.Genres, runs[s.ShowId].Episodes, runs[s.ShowId].Minutes,
                    document?.Landing?.Verdict, document?.GetsGood?.Turn, document?.Tags?.Any(t => t.Key == ShowTags.LimitedSeries) == true,
                    document?.Dna?.Traits.ToDictionary(t => t.Key, t => t.Value));
            })
            .ToList());
    }
}

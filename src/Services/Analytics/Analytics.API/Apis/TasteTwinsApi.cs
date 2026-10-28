using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="RatedShows">How many shows the caller has rated.</param>
/// <param name="Twins">How many people's ratings track theirs; picks need <see cref="InsightRules.MinUsersToShow"/>.</param>
/// <param name="Agreement">How closely, on average (0-1: 1 is the same stars on every shared show).</param>
/// <param name="Reason">Why there are no picks, when there aren't.</param>
/// <param name="AgreeOn">Shows the caller and several twins both rated highly: what the twins have in common with them.</param>
/// <param name="Picks">Shows several twins loved that the caller hasn't rated, followed or watched.</param>
public record TasteTwinsDto(int RatedShows, int Twins, double? Agreement, string? Reason, IReadOnlyList<TwinShowDto> AgreeOn,
    IReadOnlyList<TwinPickDto> Picks);

public record TwinShowDto(Guid ShowId, string Title, string? PosterUrl);

/// <param name="Loved">How many of the twins rated it 4 or 5 stars (at least <see cref="TasteTwinsApi.MinTwinsPerPick"/>).</param>
/// <param name="Stars">Their average stars for it.</param>
public record TwinPickDto(Guid ShowId, string Title, string? PosterUrl, int Loved, double Stars, double? Rating);

/// <summary>
/// Taste twins: the people whose show ratings track the caller's, and what they loved that the caller hasn't seen.
/// Computed on request from the ratings copy; nobody is named or counted alone.
/// <list type="bullet">
/// <item><b>Agreement:</b> 1 − mean |stars difference| / 4 over the shows both rated, with at least
/// <see cref="MinShared"/> in common, shrunk by n / (n + 3) so a perfect match on 3 shows doesn't beat a close one on 15.
/// The best <see cref="MaxTwins"/> with agreement of at least <see cref="MinAgreement"/> are the twins.</item>
/// <item><b>Privacy (the 5-user rule):</b> nothing is shown with fewer than <see cref="InsightRules.MinUsersToShow"/>
/// twins, and a pick needs <see cref="MinTwinsPerPick"/> twins behind it, so no one person's ratings show through.</item>
/// <item><b>Picks:</b> scored by the twins who rated it 4+, each weighted by their agreement and how much they liked it
/// (5 stars twice 4), and left out when the caller has rated, followed or watched it.</item>
/// </list>
/// </summary>
public static class TasteTwinsApi
{
    public const int MinRated = 5;
    public const int MinShared = 3;
    public const double MinAgreement = 0.75;
    public const int MaxTwins = 25;
    public const int MinTwinsPerPick = 2;
    private const int MaxPicks = 20;
    private const int MaxAgreeOn = 6;

    public static void MapTasteTwinsApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/taste-twins", GetTwinsAsync).RequireFeature(FeatureFlags.TasteTwins);

    private static async Task<Results<Ok<TasteTwinsDto>, UnauthorizedHttpResult>> GetTwinsAsync(
        ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var mine = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId).ToDictionaryAsync(r => r.ShowId, r => r.Stars, ct);
        if (mine.Count < MinRated)
        {
            return TypedResults.Ok(Empty(mine.Count, 0, null, $"Rate {MinRated - mine.Count} more {(MinRated - mine.Count == 1 ? "show" : "shows")} to find your taste twins."));
        }

        // Everyone else's ratings of the shows the caller rated, to measure agreement.
        var rated = mine.Keys.ToList();
        var others = (await db.ShowRatings.AsNoTracking()
                .Where(r => r.UserId != userId && rated.Contains(r.ShowId))
                .Select(r => new { r.UserId, r.ShowId, r.Stars })
                .ToListAsync(ct))
            .GroupBy(r => r.UserId);
        var twins = others
            .Select(g =>
            {
                var shared = g.Count();
                var agreement = 1 - g.Average(r => Math.Abs(r.Stars - mine[r.ShowId])) / 4.0;
                return (UserId: g.Key, Shared: shared, Agreement: agreement, Score: agreement * shared / (shared + 3.0));
            })
            .Where(t => t.Shared >= MinShared && t.Agreement >= MinAgreement)
            .OrderByDescending(t => t.Score)
            .Take(MaxTwins)
            .ToList();
        if (twins.Count < InsightRules.MinUsersToShow)
        {
            return TypedResults.Ok(Empty(mine.Count, twins.Count, null,
                "Not enough people here share your taste yet. Rating more shows helps."));
        }

        var weight = twins.ToDictionary(t => t.UserId, t => t.Agreement);
        var twinIds = weight.Keys.ToList();
        var theirs = await db.ShowRatings.AsNoTracking()
            .Where(r => twinIds.Contains(r.UserId) && r.Stars >= 4)
            .Select(r => new { r.UserId, r.ShowId, r.Stars })
            .ToListAsync(ct);

        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var watched = await db.Watches.AsNoTracking().Where(w => w.UserId == userId).Select(w => w.ShowId).Distinct().ToListAsync(ct);
        var known = mine.Keys.Concat(followed).Concat(watched).ToHashSet();

        var picks = theirs.Where(r => !known.Contains(r.ShowId))
            .GroupBy(r => r.ShowId)
            .Where(g => g.Select(r => r.UserId).Distinct().Count() >= MinTwinsPerPick)
            .Select(g => (ShowId: g.Key, Loved: g.Select(r => r.UserId).Distinct().Count(), Stars: g.Average(r => r.Stars),
                Score: g.Sum(r => weight[r.UserId] * (r.Stars - 3))))
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.Stars)
            .Take(MaxPicks)
            .ToList();
        var agreeOn = theirs.Where(r => mine.GetValueOrDefault(r.ShowId) >= 4)
            .GroupBy(r => r.ShowId)
            .Where(g => g.Select(r => r.UserId).Distinct().Count() >= MinTwinsPerPick)
            .OrderByDescending(g => g.Count())
            .Take(MaxAgreeOn)
            .Select(g => g.Key)
            .ToList();

        var ids = picks.Select(p => p.ShowId).Concat(agreeOn).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        return TypedResults.Ok(new TasteTwinsDto(
            mine.Count,
            twins.Count,
            Math.Round(twins.Average(t => t.Agreement), 2),
            picks.Count == 0 ? "Your twins haven't loved anything you haven't seen yet." : null,
            agreeOn.Where(shows.ContainsKey).Select(id => new TwinShowDto(id, shows[id].Title, shows[id].PosterUrl)).ToList(),
            picks.Where(p => shows.ContainsKey(p.ShowId))
                .Select(p => new TwinPickDto(p.ShowId, shows[p.ShowId].Title, shows[p.ShowId].PosterUrl, p.Loved, Math.Round(p.Stars, 1),
                    shows[p.ShowId].ExternalRating))
                .ToList()));
    }

    private static TasteTwinsDto Empty(int rated, int twins, double? agreement, string reason) =>
        new(rated, twins, agreement, reason, [], []);
}

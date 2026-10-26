using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <param name="Due">The break is at least <see cref="RefresherApi.GapDays"/> days and there's something to go back to.</param>
/// <param name="LeftOff">The furthest episode watched, in air order.</param>
/// <param name="Returns">The next new episode or announced season, when one is scheduled.</param>
/// <param name="Plan">The rewatch, in air order: episodes the user has already seen, so nothing new is given away.</param>
public record RefresherDto(
    Guid ShowId,
    string Title,
    string? PosterUrl,
    bool Due,
    DateTime LastWatchedAtUtc,
    int DaysSince,
    RefresherEpisodeDto LeftOff,
    RefresherAheadDto Ahead,
    RefresherReturnDto? Returns,
    int BudgetMinutes,
    int PlanMinutes,
    IReadOnlyList<RefresherEpisodeDto> Plan);

/// <param name="Rating">The rating it was picked by: the user's own (stars × 2) when they rated it, else the provider's.</param>
/// <param name="Why">
/// "left-off", "season-premiere", "before-you-stopped", "season-best", "last-season-finale", "last-season-best" or
/// "two-parter" (the other half of a picked episode).
/// </param>
public record RefresherEpisodeDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, int Minutes, double? Rating,
    int? YourStars, string Why);

/// <summary>Aired episodes after the furthest one watched, and the first of them.</summary>
public record RefresherAheadDto(int Episodes, int Minutes, Guid? NextEpisodeId, int? NextSeason, int? NextEpisode);

/// <param name="NewSeason">A season premiere (or an announced season), rather than the next episode of one already airing.</param>
public record RefresherReturnDto(int SeasonNumber, DateTimeOffset PremieresAtUtc, bool NewSeason);

/// <summary>
/// "Previously on…": a short rewatch for coming back to a show after a long break, cut to a time budget. It picks only
/// episodes the user has watched, so it can't spoil anything: where they left off, the premiere of that season and the
/// episode before they stopped, then that season's best, last season's finale and best. The user's own episode ratings
/// outrank the provider's, since a refresher is about what they remember. Two-parters are picked together, as in the
/// essential cut. Computed on request from the caller's own watches, like "your taste", so no minimum counts apply.
/// </summary>
public static class RefresherApi
{
    /// <summary>A break this long, or longer, is worth a refresher.</summary>
    public const int GapDays = 120;

    public const int DefaultBudget = 120;
    private const int MinBudget = 30;
    private const int MaxBudget = 600;

    /// <summary>The list on Up Next looks at this many of the user's most recently paused shows.</summary>
    private const int MaxShows = 40;

    /// <summary>Each season contributes up to this many of its best episodes before the rest are considered.</summary>
    private const int TopBest = 2;

    public static void MapRefresherApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{showId:guid}/refresher", GetOneAsync).RequireFeature(FeatureFlags.Refresher);
        api.MapGet("/me/refreshers", GetAllAsync).RequireFeature(FeatureFlags.Refresher);
    }

    /// <summary>204 when the user hasn't watched the show, or has seen all of it and nothing more is coming.</summary>
    private static async Task<Results<Ok<RefresherDto>, NoContent, UnauthorizedHttpResult, ValidationProblem>> GetOneAsync(
        Guid showId, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, int minutes = DefaultBudget, CancellationToken ct = default)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        if (minutes is < MinBudget or > MaxBudget)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["minutes"] = [$"Use {MinBudget} to {MaxBudget} minutes."] });
        }

        return await BuildAsync(db, userId, showId, minutes, time.GetUtcNow(), ct) is { } refresher
            ? TypedResults.Ok(refresher)
            : TypedResults.NoContent();
    }

    /// <summary>Followed shows due a refresher: the ones coming back soonest first, then the most recently paused.</summary>
    private static async Task<Results<Ok<List<RefresherDto>>, UnauthorizedHttpResult>> GetAllAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var now = time.GetUtcNow();
        var cutoff = now.UtcDateTime.AddDays(-GapDays);
        var followed = db.Follows.Where(f => f.UserId == userId).Select(f => f.ShowId);
        var paused = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && followed.Contains(w.ShowId))
            .GroupBy(w => w.ShowId)
            .Select(g => new { ShowId = g.Key, Last = g.Max(w => w.WatchedAtUtc) })
            .Where(g => g.Last < cutoff)
            .OrderByDescending(g => g.Last)
            .Take(MaxShows)
            .ToListAsync(ct);

        var due = new List<RefresherDto>();
        foreach (var show in paused)
        {
            if (await BuildAsync(db, userId, show.ShowId, DefaultBudget, now, ct) is { Due: true } refresher)
            {
                due.Add(refresher);
            }
        }

        return TypedResults.Ok(due
            .OrderBy(r => r.Returns is null)
            .ThenBy(r => r.Returns?.PremieresAtUtc)
            .ThenByDescending(r => r.LastWatchedAtUtc)
            .ToList());
    }

    private static async Task<RefresherDto?> BuildAsync(AnalyticsContext db, string userId, Guid showId, int budget, DateTimeOffset now, CancellationToken ct)
    {
        var watches = await db.Watches.AsNoTracking().Where(w => w.UserId == userId && w.ShowId == showId).ToListAsync(ct);
        var show = watches.Count == 0 ? null : await db.Shows.AsNoTracking().SingleOrDefaultAsync(s => s.ShowId == showId, ct);
        if (show is null)
        {
            return null;
        }

        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowId == showId && e.SeasonNumber > 0)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .ToListAsync(ct);
        var aired = episodes.Where(e => e.AirsAtUtc <= now).ToList();
        var watched = watches.Select(w => w.EpisodeId).ToHashSet();
        var furthestIndex = aired.FindLastIndex(e => watched.Contains(e.EpisodeId));
        if (furthestIndex < 0)
        {
            return null;
        }

        var announced = await db.AnnouncedSeasons.AsNoTracking()
            .Where(s => s.ShowId == showId && s.PremieresAtUtc > now)
            .OrderBy(s => s.PremieresAtUtc)
            .FirstOrDefaultAsync(ct);
        var nextNew = episodes.Where(e => e.AirsAtUtc > now).MinBy(e => e.AirsAtUtc);
        var returns = (nextNew, announced) switch
        {
            ({ } e, { } s) when s.PremieresAtUtc < e.AirsAtUtc => new RefresherReturnDto(s.SeasonNumber, s.PremieresAtUtc!.Value, true),
            ({ } e, _) => new RefresherReturnDto(e.SeasonNumber, e.AirsAtUtc!.Value, e.EpisodeNumber == 1),
            (null, { } s) => new RefresherReturnDto(s.SeasonNumber, s.PremieresAtUtc!.Value, true),
            _ => null
        };

        var ahead = aired.Skip(furthestIndex + 1).ToList();
        if (ahead.Count == 0 && returns is null)
        {
            return null;
        }

        var stars = await db.EpisodeRatings.AsNoTracking()
            .Where(r => r.UserId == userId && r.ShowId == showId)
            .ToDictionaryAsync(r => r.EpisodeId, r => r.Stars, ct);

        // Missing runtimes count as the show's typical one, as in the essential cut.
        var runtimes = aired.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
        var typical = runtimes.Count > 0 ? runtimes[runtimes.Count / 2] : 0;
        int Minutes(EpisodeCopy e) => e.RuntimeMinutes is > 0 ? e.RuntimeMinutes.Value : typical;
        double? Rating(EpisodeCopy e) => stars.TryGetValue(e.EpisodeId, out var s) ? s * 2.0 : e.ExternalRating;
        RefresherEpisodeDto Dto(EpisodeCopy e, string why) => new(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title, Minutes(e),
            Rating(e), stars.TryGetValue(e.EpisodeId, out var s) ? s : null, why);

        var seen = aired.Take(furthestIndex + 1).Where(e => watched.Contains(e.EpisodeId)).ToList();
        var furthest = aired[furthestIndex];
        var plan = Plan(seen, furthest, aired, budget, Minutes, Rating);
        var lastWatched = watches.Max(w => w.WatchedAtUtc);
        var daysSince = (int)(now.UtcDateTime - lastWatched).TotalDays;

        return new RefresherDto(
            showId, show.Title, show.PosterUrl,
            daysSince >= GapDays,
            lastWatched, daysSince,
            Dto(furthest, "left-off"),
            new RefresherAheadDto(ahead.Count, ahead.Sum(Minutes), ahead.FirstOrDefault()?.EpisodeId, ahead.FirstOrDefault()?.SeasonNumber,
                ahead.FirstOrDefault()?.EpisodeNumber),
            returns,
            budget,
            plan.Sum(p => Minutes(p.Episode)),
            plan.Select(p => Dto(p.Episode, p.Why)).ToList());
    }

    /// <summary>
    /// Candidates in order of how much they bring back, taken greedily while they fit the budget (with the same small
    /// overrun as Tonight's plan). Where they left off is always taken, even when it alone is over budget.
    /// </summary>
    private static List<(EpisodeCopy Episode, string Why)> Plan(List<EpisodeCopy> seen, EpisodeCopy furthest, List<EpisodeCopy> aired,
        int budget, Func<EpisodeCopy, int> minutes, Func<EpisodeCopy, double?> rating)
    {
        var focus = seen.Where(e => e.SeasonNumber == furthest.SeasonNumber).ToList();
        var previousNumber = seen.Where(e => e.SeasonNumber < furthest.SeasonNumber).Select(e => (int?)e.SeasonNumber).Max();
        var previous = seen.Where(e => e.SeasonNumber == previousNumber).ToList();
        List<EpisodeCopy> Best(List<EpisodeCopy> season) => season.Where(e => rating(e) is not null).OrderByDescending(rating).ToList();
        var focusBest = Best(focus);
        var previousBest = Best(previous);

        var candidates = new List<(EpisodeCopy Episode, string Why)> { (furthest, "left-off"), (focus[0], "season-premiere") };
        if (focus.IndexOf(furthest) is var at and >= 2)
        {
            candidates.Add((focus[at - 1], "before-you-stopped"));
        }
        candidates.AddRange(focusBest.Take(TopBest).Select(e => (e, "season-best")));
        if (previous.Count > 0 && previous[^1] == aired.Last(e => e.SeasonNumber == previousNumber))
        {
            candidates.Add((previous[^1], "last-season-finale"));
        }
        candidates.AddRange(previousBest.Take(TopBest).Select(e => (e, "last-season-best")));
        candidates.AddRange(focusBest.Skip(TopBest).Select(e => (e, "season-best")));
        candidates.AddRange(previousBest.Skip(TopBest).Select(e => (e, "last-season-best")));

        var limit = budget + Math.Max(5, budget / 12);
        var picked = new Dictionary<Guid, (EpisodeCopy Episode, string Why)>();
        var total = 0;
        foreach (var (episode, why) in candidates)
        {
            if (picked.ContainsKey(episode.EpisodeId))
            {
                continue;
            }

            var group = new List<(EpisodeCopy Episode, string Why)> { (episode, why) };
            if (PartnerOf(episode, seen) is { } partner && !picked.ContainsKey(partner.EpisodeId))
            {
                group.Add((partner, "two-parter"));
            }

            var cost = group.Sum(g => minutes(g.Episode));
            if (picked.Count > 0 && total + cost > limit)
            {
                continue;
            }
            foreach (var g in group)
            {
                picked[g.Episode.EpisodeId] = g;
            }
            total += cost;
        }

        return picked.Values.OrderBy(p => p.Episode.SeasonNumber).ThenBy(p => p.Episode.EpisodeNumber).ToList();
    }

    /// <summary>The other half of a two-parter, when the user has seen it too.</summary>
    private static EpisodeCopy? PartnerOf(EpisodeCopy episode, List<EpisodeCopy> seen)
    {
        if (!EssentialCutApi.PartPattern.IsMatch(episode.Title))
        {
            return null;
        }

        return seen.FirstOrDefault(e => e.SeasonNumber == episode.SeasonNumber
            && Math.Abs(e.EpisodeNumber - episode.EpisodeNumber) == 1
            && EssentialCutApi.PartPattern.IsMatch(e.Title));
    }
}

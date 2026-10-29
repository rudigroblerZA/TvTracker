using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker;

/// <summary>
/// Scores "Call it" predictions (<see cref="SeasonCall"/>) once their season is over: the season's average provider
/// rating over its aired regular episodes, as the season stats and verdict compute it. Over means the season verdict
/// called it (see <c>SeasonVerdictInsight</c>), a later season has aired, or the show has ended. A season needs
/// <see cref="MinRatedEpisodes"/> rated episodes to be scored; until then its calls wait. Run after each insights run,
/// so the verdicts it reads are the latest.
/// </summary>
public sealed class SeasonCallScorer(AnalyticsContext db, TimeProvider time, ILogger<SeasonCallScorer> logger)
{
    private const int MinRatedEpisodes = 3;

    public async Task ScoreAsync(CancellationToken ct)
    {
        var open = await db.SeasonCalls.AsNoTracking().Where(c => c.ScoredAtUtc == null)
            .Select(c => new { c.ShowId, c.SeasonNumber }).Distinct().ToListAsync(ct);
        if (open.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var showIds = open.Select(o => o.ShowId).Distinct().ToList();
        var ended = await db.Shows.AsNoTracking()
            .Where(s => showIds.Contains(s.ShowId) && (s.Status == ShowStatus.Ended || s.Status == ShowStatus.Cancelled))
            .Select(s => s.ShowId).ToListAsync(ct);
        var episodes = (await db.Episodes.AsNoTracking()
                .Where(e => showIds.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc <= now)
                .Select(e => new { e.ShowId, e.SeasonNumber, e.ExternalRating })
                .ToListAsync(ct))
            .ToLookup(e => e.ShowId);
        var verdicts = (await db.ShowInsights.AsNoTracking().Where(d => showIds.Contains(d.ShowId))
                .Select(d => new { d.ShowId, d.Document }).ToListAsync(ct))
            .ToDictionary(d => d.ShowId, d => JsonSerializer.Deserialize<ShowInsights>(d.Document, JsonSerializerOptions.Web)?.SeasonVerdict?.Number);

        var scored = 0;
        foreach (var season in open)
        {
            var aired = episodes[season.ShowId].ToList();
            var over = verdicts.GetValueOrDefault(season.ShowId) == season.SeasonNumber
                || aired.Any(e => e.SeasonNumber > season.SeasonNumber)
                || ended.Contains(season.ShowId);
            var ratings = aired.Where(e => e.SeasonNumber == season.SeasonNumber && e.ExternalRating is not null)
                .Select(e => e.ExternalRating!.Value).ToList();
            if (!over || ratings.Count < MinRatedEpisodes)
            {
                continue;
            }

            var actual = Math.Round(ratings.Average(), 2);
            scored += await db.SeasonCalls
                .Where(c => c.ShowId == season.ShowId && c.SeasonNumber == season.SeasonNumber && c.ScoredAtUtc == null)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.Actual, actual).SetProperty(x => x.ScoredAtUtc, now.UtcDateTime), ct);
        }
        if (scored > 0)
        {
            logger.LogInformation("Scored {Calls} season calls", scored);
        }
    }
}

using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.Worker.Insights;

/// <summary>
/// Reads what the engine's rules are given (<see cref="InsightSources"/>, and each show's
/// <see cref="ShowInsightContext"/>) out of Analytics' database. The engine itself never touches it.
/// </summary>
public static class InsightSourcesLoader
{
    public static async Task<InsightSources> LoadAsync(AnalyticsContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cast = await db.Cast.AsNoTracking().ToListAsync(ct);
        var careers = await db.Careers.AsNoTracking().ToDictionaryAsync(c => c.PersonId, ct);
        var follows = await db.Follows.AsNoTracking().Select(f => new { f.ShowId, f.UserId, f.FollowedAtUtc }).ToListAsync(ct);
        var creatives = await db.Creatives.AsNoTracking().ToListAsync(ct);

        return new InsightSources
        {
            Now = now,
            Shows = await db.Shows.AsNoTracking().ToDictionaryAsync(s => s.ShowId, ct),
            Cast = cast,
            CastByShow = cast.ToLookup(c => c.ShowId),
            CastByPerson = cast.ToLookup(InsightSources.PersonKey),
            CastByCharacter = cast
                .SelectMany(c => c.CharacterExternalIds.Select(id => (Id: id, Credit: c)))
                .ToLookup(x => x.Id, x => x.Credit),
            Creatives = creatives,
            ProlificMakerByShow = creatives
                .Where(c => careers.TryGetValue(c.PersonId, out var career) && career.Shows >= ShowTags.ProlificMinShows)
                .GroupBy(c => c.ShowId)
                .ToDictionary(g => g.Key, g => g
                    .Select(c => (Creative: c, Career: careers[c.PersonId]))
                    .OrderByDescending(x => x.Career.Shows)
                    // Makers with as many shows are common, and the rows come in no order: the tag names the maker, so
                    // without this it could name a different one from run to run.
                    .ThenBy(x => x.Creative.PersonId)
                    .First()),
            EpisodeRatings = await db.EpisodeRatings.AsNoTracking()
                .GroupBy(r => r.EpisodeId)
                .Select(g => new { EpisodeId = g.Key, Sum = g.Sum(r => r.Stars), Count = g.Count() })
                .ToDictionaryAsync(r => r.EpisodeId, r => (r.Sum, r.Count), ct),
            ShowRatings = await db.ShowRatings.AsNoTracking()
                .GroupBy(r => r.ShowId)
                .Select(g => new { ShowId = g.Key, Sum = g.Sum(r => r.Stars), Count = g.Count() })
                .ToDictionaryAsync(r => r.ShowId, r => (r.Sum, r.Count), ct),
            ShowStarCounts = (await db.ShowRatings.AsNoTracking()
                    .GroupBy(r => new { r.ShowId, r.Stars })
                    .Select(g => new { g.Key.ShowId, g.Key.Stars, Count = g.Count() })
                    .ToListAsync(ct))
                .ToLookup(r => r.ShowId, r => (r.Stars, r.Count)),
            AnnouncedSeasons = (await db.AnnouncedSeasons.AsNoTracking().ToListAsync(ct)).ToLookup(a => a.ShowId),
            SeasonHistory = (await db.Episodes.AsNoTracking()
                    .Where(e => e.SeasonNumber > 0)
                    .GroupBy(e => new { e.ShowId, e.SeasonNumber })
                    .Select(g => new
                    {
                        g.Key.ShowId,
                        g.Key.SeasonNumber,
                        Episodes = g.Count(),
                        Aired = g.Count(e => e.AirsAtUtc <= now),
                        First = g.Min(e => e.AirsAtUtc),
                        LastAired = g.Where(e => e.AirsAtUtc <= now).Max(e => e.AirsAtUtc),
                        Rated = g.Count(e => e.AirsAtUtc <= now && e.ExternalRating != null),
                        RatingSum = g.Where(e => e.AirsAtUtc <= now && e.ExternalRating != null).Sum(e => e.ExternalRating)
                    })
                    .ToListAsync(ct))
                .OrderBy(s => s.SeasonNumber)
                .Select(s => new SeasonRecord(s.ShowId, s.SeasonNumber, s.Episodes, s.Aired, s.First, s.LastAired,
                    s.Rated >= SeasonSummary.MinRatedEpisodes ? Math.Round(s.RatingSum!.Value / s.Rated, 2) : null))
                .ToLookup(s => s.ShowId),
            FollowDates = follows.ToLookup(f => f.ShowId, f => f.FollowedAtUtc),
            FollowersByShow = follows.ToLookup(f => f.ShowId, f => f.UserId)
        };
    }

    /// <summary>
    /// The shows' episodes and our users' watches, as one context per show, in the order given.
    /// </summary>
    public static async Task<List<ShowInsightContext>> LoadShowsAsync(this InsightSources sources, AnalyticsContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var episodes = (await db.Episodes.AsNoTracking().Where(e => ids.Contains(e.ShowId)).ToListAsync(ct)).ToLookup(e => e.ShowId);
        var watches = (await db.Watches.AsNoTracking().Where(w => ids.Contains(w.ShowId)).ToListAsync(ct)).ToLookup(w => w.ShowId);
        return ids.Select(id => new ShowInsightContext(sources, sources.Shows[id], episodes[id].ToList(), watches[id].ToList())).ToList();
    }
}

using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.Worker.Insights;

/// <summary>
/// The data every insight reads, loaded once per run: Analytics' copy of the catalog and our users' ratings and
/// follows, already grouped the way the insights look them up. Episodes and watches are loaded per batch of shows
/// instead (<see cref="LoadShowsAsync"/>), since they're the bulk of it. An insight that needs new data adds it here.
/// </summary>
public sealed class InsightSources
{
    /// <summary>The run's clock: what has aired, what's upcoming, and how recent a follow or watch is.</summary>
    public required DateTimeOffset Now { get; init; }

    public DateTime ComputedAtUtc => Now.UtcDateTime;

    public required Dictionary<Guid, ShowCopy> Shows { get; init; }

    public required List<CastCopy> Cast { get; init; }
    public required ILookup<Guid, CastCopy> CastByShow { get; init; }

    /// <summary>Credits by <see cref="PersonKey"/>.</summary>
    public required ILookup<string, CastCopy> CastByPerson { get; init; }

    /// <summary>Credits by the provider's character id.</summary>
    public required ILookup<string, CastCopy> CastByCharacter { get; init; }

    /// <summary>
    /// Each show's most prolific maker: credited behind the camera on at least <see cref="ShowTags.ProlificMinShows"/>
    /// shows; absent when it has none.
    /// </summary>
    public required Dictionary<Guid, (CreativeCopy Creative, PersonCareerCopy Career)> ProlificMakerByShow { get; init; }

    /// <summary>Our users' episode stars, summed and counted per episode, before the privacy rule (see <see cref="ShowInsightContext.OurEpisodeRatings"/>).</summary>
    public required Dictionary<Guid, (int Sum, int Count)> EpisodeRatings { get; init; }

    public required Dictionary<Guid, (int Sum, int Count)> ShowRatings { get; init; }

    /// <summary>How many of our users gave each show each number of stars.</summary>
    public required ILookup<Guid, (int Stars, int Count)> ShowStarCounts { get; init; }

    public required ILookup<Guid, AnnouncedSeason> AnnouncedSeasons { get; init; }

    /// <summary>
    /// Every show's regular seasons in order, summarised: the history the forecasts learn from and backtest on
    /// (see <see cref="Forecasts"/>).
    /// </summary>
    public required ILookup<Guid, SeasonRecord> SeasonHistory { get; init; }

    /// <summary>The forecast models, trained on <see cref="SeasonHistory"/> on first use in a run.</summary>
    public ForecastModels Forecasts => field ??= ForecastModels.Train(this);

    public required ILookup<Guid, DateTime> FollowDates { get; init; }

    public required ILookup<Guid, string> FollowersByShow { get; init; }

    public static async Task<InsightSources> LoadAsync(AnalyticsContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cast = await db.Cast.AsNoTracking().ToListAsync(ct);
        var careers = await db.Careers.AsNoTracking().ToDictionaryAsync(c => c.PersonId, ct);
        var follows = await db.Follows.AsNoTracking().Select(f => new { f.ShowId, f.UserId, f.FollowedAtUtc }).ToListAsync(ct);

        return new InsightSources
        {
            Now = now,
            Shows = await db.Shows.AsNoTracking().ToDictionaryAsync(s => s.ShowId, ct),
            Cast = cast,
            CastByShow = cast.ToLookup(c => c.ShowId),
            CastByPerson = cast.ToLookup(PersonKey),
            CastByCharacter = cast
                .SelectMany(c => c.CharacterExternalIds.Select(id => (Id: id, Credit: c)))
                .ToLookup(x => x.Id, x => x.Credit),
            ProlificMakerByShow = (await db.Creatives.AsNoTracking().ToListAsync(ct))
                .Where(c => careers.TryGetValue(c.PersonId, out var career) && career.Shows >= ShowTags.ProlificMinShows)
                .GroupBy(c => c.ShowId)
                .ToDictionary(g => g.Key, g => g
                    .Select(c => (Creative: c, Career: careers[c.PersonId]))
                    .OrderByDescending(x => x.Career.Shows)
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

    /// <summary>The shows' episodes and our users' watches, as one context per show, in the order given.</summary>
    public async Task<List<ShowInsightContext>> LoadShowsAsync(AnalyticsContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var episodes = (await db.Episodes.AsNoTracking().Where(e => ids.Contains(e.ShowId)).ToListAsync(ct)).ToLookup(e => e.ShowId);
        var watches = (await db.Watches.AsNoTracking().Where(w => ids.Contains(w.ShowId)).ToListAsync(ct)).ToLookup(w => w.ShowId);
        return ids.Select(id => new ShowInsightContext(this, Shows[id], episodes[id].ToList(), watches[id].ToList())).ToList();
    }

    /// <summary>
    /// Who a credit is: the catalog's person id, so two actors who share a name aren't shown as shared cast; the name
    /// for credits stored before ids were sent (the next catalog replay fills those in).
    /// </summary>
    public static string PersonKey(CastCopy credit) => credit.PersonId?.ToString() ?? $"name:{credit.PersonName}";
}

/// <summary>A regular season, summarised for the forecasts.</summary>
/// <param name="LastAired">The latest aired episode's air time; null before any aired.</param>
/// <param name="Rating">The provider's average for its aired episodes; null with fewer than <see cref="SeasonSummary.MinRatedEpisodes"/> rated.</param>
public record SeasonRecord(Guid ShowId, int Number, int Episodes, int Aired, DateTimeOffset? First, DateTimeOffset? LastAired, double? Rating)
{
    /// <summary>Every episode has aired.</summary>
    public bool Complete => Aired > 0 && Aired == Episodes;
}

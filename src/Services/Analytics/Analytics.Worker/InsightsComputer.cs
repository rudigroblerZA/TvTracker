using System.Text.Json;
using Analytics.Data;
using Microsoft.EntityFrameworkCore;

namespace Analytics.Worker;

/// <summary>
/// Computes every show's insights, the premieres list and the top-episode rankings from Analytics' copies of
/// the catalog and our users' ratings. Runs hourly; the API only reads what this writes.
/// </summary>
public sealed class InsightsComputer(AnalyticsContext db, TimeProvider time, ILogger<InsightsComputer> logger)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int ShowBatchSize = 100;
    private const int TopEpisodesPerShow = 5;
    private const int SharedCastPerShow = 12;
    private const int TopEpisodesPerList = 50;

    /// <summary>A show contributes at most this many episodes to a top-episodes list, so one show can't fill it.</summary>
    private const int MaxListEpisodesPerShow = 2;

    /// <summary>A season's rating counts for best/worst/trend only with at least this many rated episodes.</summary>
    private const int MinRatedEpisodesPerSeason = 3;

    /// <summary>Shows need this many rated episodes before theirs can appear in the top-episodes lists.</summary>
    private const int MinRatedEpisodesForLists = 5;

    /// <summary>
    /// ...and at least this share of their aired episodes rated. Sparsely rated shows get their episode ratings from a
    /// few devoted fans (e.g. episodes averaging 9.7 for a show rated 6.4), which would otherwise top every list.
    /// </summary>
    private const double MinRatedShareForLists = 0.5;

    /// <summary>Seasons premiering within this many days make the premieres list.</summary>
    private const int PremiereWindowDays = 90;

    /// <summary>Change in rating per season (0-10 scale) beyond which a show is "rising" or "falling".</summary>
    private const double TrendThreshold = 0.05;

    private const int DiscoveryListLength = 30;

    /// <summary>
    /// Hidden gems: shows rated at least this, outside the catalog's most popular <see cref="HiddenGemPopularShare"/>
    /// on the provider (TVmaze's popularity tops out at 100 for the famous ones), and followed by fewer than the
    /// minimum number of our users.
    /// </summary>
    private const double HiddenGemMinRating = 7.5;

    private const double HiddenGemPopularShare = 0.4;

    /// <summary>The consistency lists only rank shows rated at least this, so dull-but-steady shows don't top them.</summary>
    private const double MostConsistentMinRating = 7.5;

    private const double RollercoasterMinRating = 7.0;

    private const int CoStarsPerPerson = 6;

    public async Task<int> ComputeAllAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var computedAt = now.UtcDateTime;

        var shows = await db.Shows.AsNoTracking().ToDictionaryAsync(s => s.ShowId, ct);
        var cast = await db.Cast.AsNoTracking().ToListAsync(ct);
        var castByShow = cast.ToLookup(c => c.ShowId);
        var showsByPerson = cast.ToLookup(PersonKey);
        var showsByCharacter = cast
            .SelectMany(c => c.CharacterExternalIds.Select(id => (Id: id, Credit: c)))
            .ToLookup(x => x.Id, x => x.Credit);

        var episodeRatings = await db.EpisodeRatings.AsNoTracking()
            .GroupBy(r => r.EpisodeId)
            .Select(g => new { EpisodeId = g.Key, Sum = g.Sum(r => r.Stars), Count = g.Count() })
            .ToDictionaryAsync(r => r.EpisodeId, r => (r.Sum, r.Count), ct);
        var showRatings = await db.ShowRatings.AsNoTracking()
            .GroupBy(r => r.ShowId)
            .Select(g => new { ShowId = g.Key, Sum = g.Sum(r => r.Stars), Count = g.Count() })
            .ToDictionaryAsync(r => r.ShowId, r => (r.Sum, r.Count), ct);
        var announced = (await db.AnnouncedSeasons.AsNoTracking().ToListAsync(ct)).ToLookup(a => a.ShowId);
        var starCounts = (await db.ShowRatings.AsNoTracking()
                .GroupBy(r => new { r.ShowId, r.Stars })
                .Select(g => new { g.Key.ShowId, g.Key.Stars, Count = g.Count() })
                .ToListAsync(ct))
            .ToLookup(r => r.ShowId, r => (r.Stars, r.Count));
        var follows = await db.Follows.AsNoTracking().Select(f => new { f.ShowId, f.UserId, f.FollowedAtUtc }).ToListAsync(ct);
        var followDates = follows.ToLookup(f => f.ShowId, f => f.FollowedAtUtc);
        var followersByShow = follows.ToLookup(f => f.ShowId, f => f.UserId);

        var premieres = new List<PremiereRow>();
        var listCandidates = new List<(ShowCopy Show, EpisodeCopy Episode, (int Sum, int Count) Ours, double Score)>();
        var discoveryCandidates = new List<DiscoveryCandidate>();
        var runs = new Dictionary<Guid, RunFacts>();

        foreach (var batch in shows.Keys.Chunk(ShowBatchSize))
        {
            var ids = batch.ToList();
            var episodesByShow = (await db.Episodes.AsNoTracking().Where(e => ids.Contains(e.ShowId)).ToListAsync(ct))
                .ToLookup(e => e.ShowId);
            var existing = await db.ShowInsights.Where(i => ids.Contains(i.ShowId)).ToDictionaryAsync(i => i.ShowId, ct);
            var watchesByShow = (await db.Watches.AsNoTracking().Where(w => ids.Contains(w.ShowId)).ToListAsync(ct))
                .ToLookup(w => w.ShowId);

            foreach (var showId in ids)
            {
                var show = shows[showId];
                var episodes = episodesByShow[showId].ToList();
                var ours = (Func<Guid, (int Sum, int Count)>)(id => Visible(episodeRatings.GetValueOrDefault(id)));

                var momentum = MomentumFor(followDates[showId].ToList(), now);
                var insights = Compute(show, episodes, ours, showRatings.GetValueOrDefault(showId), announced[showId],
                    SharedCastFor(show, castByShow[showId], showsByPerson, showsByCharacter, shows), now, computedAt) with
                {
                    Histograms = HistogramsFor(episodes, starCounts[showId], now),
                    Viewing = ViewingFor(episodes, watchesByShow[showId].ToList(), now),
                    Momentum = momentum
                };

                var document = JsonSerializer.Serialize(insights, JsonOptions);
                if (existing.TryGetValue(showId, out var row))
                {
                    row.Document = document;
                    row.ComputedAtUtc = computedAt;
                }
                else
                {
                    db.ShowInsights.Add(new ShowInsightsRow { ShowId = showId, Document = document, ComputedAtUtc = computedAt });
                }

                if (insights.NextPremiere is { } p && p.PremieresAtUtc <= now.AddDays(PremiereWindowDays))
                {
                    premieres.Add(new PremiereRow
                    {
                        ShowId = showId,
                        SeasonNumber = p.SeasonNumber,
                        ShowTitle = show.Title,
                        PosterUrl = show.PosterUrl,
                        Genres = show.Genres,
                        PremieresAtUtc = p.PremieresAtUtc,
                        ShowExternalRating = show.ExternalRating,
                        DaysSincePreviousSeason = p.DaysSincePreviousSeason,
                        CatchUpEpisodes = p.CatchUpEpisodes,
                        CatchUpRuntimeMinutes = p.CatchUpRuntimeMinutes,
                        RecentFollows = momentum?.RecentFollows
                    });
                }

                var rated = Aired(episodes, now).Where(e => e.ExternalRating is not null).ToList();
                var airedCount = Aired(episodes, now).Count();
                var wellRated = show.ExternalRating is not null
                    && rated.Count >= MinRatedEpisodesForLists
                    && rated.Count >= airedCount * MinRatedShareForLists;
                List<(EpisodeCopy Finale, double Score)> finales = [];
                if (wellRated)
                {
                    var showRating = show.ExternalRating!.Value;
                    var episodeMean = rated.Average(e => e.ExternalRating!.Value);
                    listCandidates.AddRange(rated
                        .OrderByDescending(e => e.ExternalRating)
                        .Take(MaxListEpisodesPerShow)
                        .Select(e => (show, e, ours(e.EpisodeId), showRating + (e.ExternalRating!.Value - episodeMean))));
                    finales = FinalesOf(insights.Seasons, episodes)
                        .Select(f => (f, showRating + (f.ExternalRating!.Value - episodeMean)))
                        .OrderByDescending(f => f.Item2)
                        .Take(MaxListEpisodesPerShow)
                        .ToList();
                }

                runs[showId] = insights.Run;
                discoveryCandidates.Add(new DiscoveryCandidate(show, airedCount, wellRated, insights.Patterns?.Consistency,
                    followersByShow[showId].Count(), finales));
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        var people = PersonInsightsRows(cast, shows, runs, followersByShow, computedAt);
        await ReplaceListsAsync(premieres, TopEpisodeRows(listCandidates), DiscoveryRows(discoveryCandidates), people, ct);

        logger.LogInformation(
            "Computed insights for {Shows} shows and {People} people: {Premieres} upcoming premieres, {Candidates} top-episode candidates",
            shows.Count, people.Count, premieres.Count, listCandidates.Count);
        return shows.Count;
    }

    private static ShowInsights Compute(
        ShowCopy show, List<EpisodeCopy> episodes, Func<Guid, (int Sum, int Count)> ours, (int Sum, int Count) showOurs,
        IEnumerable<AnnouncedSeason> announced, IReadOnlyList<SharedCast> sharedCast, DateTimeOffset now, DateTime computedAt)
    {
        var aired = episodes.Where(e => e.AirsAtUtc <= now).ToList();

        var seasons = episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var seasonAired = g.Where(e => e.AirsAtUtc <= now).ToList();
                var rated = seasonAired.Where(e => e.ExternalRating is not null).ToList();
                var seasonOurs = g.Select(e => ours(e.EpisodeId)).Aggregate((Sum: 0, Count: 0), (a, b) => (a.Sum + b.Sum, a.Count + b.Count));
                return (Stats: new SeasonStats(
                        g.Key,
                        g.Count(),
                        seasonAired.Count,
                        g.Min(e => e.AirsAtUtc),
                        g.Max(e => e.AirsAtUtc),
                        seasonAired.Sum(e => e.RuntimeMinutes ?? 0),
                        Pair(rated.Count > 0 ? Round(rated.Average(e => e.ExternalRating!.Value)) : null, seasonOurs)),
                    RatedEpisodes: rated.Count);
            })
            .ToList();

        var (best, worst) = BestAndWorst(seasons);

        return new ShowInsights(
            show.ShowId,
            show.Title,
            computedAt,
            Pair(show.ExternalRating, showOurs),
            seasons.Select(s => s.Stats).ToList(),
            best,
            worst,
            Trend(seasons),
            aired
                .Where(e => e.ExternalRating is not null || ours(e.EpisodeId).Count >= InsightRules.MinRatingsToShow)
                .OrderByDescending(e => e.ExternalRating ?? 0)
                .ThenByDescending(e => Average(ours(e.EpisodeId)) ?? 0)
                .Take(TopEpisodesPerShow)
                .Select(e => new TopEpisode(e.EpisodeId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirsAtUtc, Pair(e.ExternalRating, ours(e.EpisodeId))))
                .ToList(),
            RunFactsFor(seasons.Select(s => s.Stats).ToList(), aired),
            NextPremiere(seasons.Select(s => s.Stats).ToList(), announced, aired, now),
            sharedCast,
            Patterns: PatternsFor(seasons, aired));
    }

    /// <summary>Finales, each season's best episode, consistency and the show's arc, from the provider's ratings.</summary>
    private static EpisodePatterns PatternsFor(List<(SeasonStats Stats, int RatedEpisodes)> seasons, List<EpisodeCopy> aired)
    {
        var rated = aired.Where(e => e.ExternalRating is not null).ToList();
        var ratedBySeason = rated.ToLookup(e => e.SeasonNumber);

        // A season's last episode is its finale only once the whole season has aired. It's compared with the
        // season's other episodes.
        var finaleDifferences = seasons
            .Where(s => s.RatedEpisodes >= MinRatedEpisodesPerSeason && s.Stats.AiredCount == s.Stats.EpisodeCount)
            .Select(s =>
            {
                var finale = aired.Where(e => e.SeasonNumber == s.Stats.Number).MaxBy(e => e.EpisodeNumber);
                var others = ratedBySeason[s.Stats.Number].Where(e => e != finale).ToList();
                return finale?.ExternalRating is { } rating && others.Count > 0
                    ? rating - others.Average(e => e.ExternalRating!.Value)
                    : (double?)null;
            })
            .OfType<double>()
            .ToList();
        var finales = finaleDifferences.Count == 0
            ? null
            : new FinaleStats(finaleDifferences.Count, finaleDifferences.Count(d => d > 0), Round(finaleDifferences.Average()));

        var seasonBests = ratedBySeason
            .OrderBy(g => g.Key)
            .Select(g => g.OrderByDescending(e => e.ExternalRating).ThenBy(e => e.EpisodeNumber).First())
            .Select(e => new SeasonBest(e.SeasonNumber, e.EpisodeId, e.EpisodeNumber, e.Title, e.ExternalRating!.Value))
            .ToList();

        Consistency? consistency = null;
        if (rated.Count >= InsightRules.MinRatedEpisodesForConsistency)
        {
            var mean = rated.Average(e => e.ExternalRating!.Value);
            var spread = Round(Math.Sqrt(rated.Sum(e => Math.Pow(e.ExternalRating!.Value - mean, 2)) / (rated.Count - 1)));
            consistency = new Consistency(spread,
                spread < InsightRules.SteadySpread ? "steady" : spread >= InsightRules.RollercoasterSpread ? "rollercoaster" : "varied");
        }

        return new EpisodePatterns(finales, seasonBests, consistency, ArcFor(seasons));
    }

    /// <summary>
    /// The best-rated season, and the season a decline set in: the first after the peak from which every rated
    /// season stayed at least <see cref="InsightRules.DeclineMargin"/> below it.
    /// </summary>
    private static Arc? ArcFor(List<(SeasonStats Stats, int RatedEpisodes)> seasons)
    {
        var points = seasons
            .Where(s => s.Stats.Rating.External is not null && s.RatedEpisodes >= MinRatedEpisodesPerSeason)
            .Select(s => (s.Stats.Number, Rating: s.Stats.Rating.External!.Value))
            .ToList();
        if (points.Count < 3)
        {
            return null;
        }

        var peakIndex = points.FindIndex(p => p.Rating == points.Max(x => x.Rating));
        var peak = points[peakIndex];

        int? declineFrom = null;
        for (var i = peakIndex + 1; i < points.Count; i++)
        {
            if (points.Skip(i).All(p => p.Rating <= peak.Rating - InsightRules.DeclineMargin))
            {
                declineFrom = points[i].Number;
                break;
            }
        }

        return new Arc(peak.Number, peak.Rating, declineFrom, peakIndex == points.Count - 1);
    }

    /// <summary>
    /// Best and worst season by the provider's ratings, or by ours when the provider has none.
    /// Needs two seasons with enough rated episodes; worst is omitted when it would equal best.
    /// </summary>
    private static (SeasonHighlight? Best, SeasonHighlight? Worst) BestAndWorst(List<(SeasonStats Stats, int RatedEpisodes)> seasons)
    {
        var byExternal = seasons
            .Where(s => s.Stats.Rating.External is not null && s.RatedEpisodes >= MinRatedEpisodesPerSeason)
            .Select(s => (s.Stats.Number, Rating: s.Stats.Rating.External!.Value))
            .ToList();
        var (source, ranked) = byExternal.Count >= 2
            ? ("external", byExternal)
            : ("ours", seasons.Where(s => s.Stats.Rating.Ours is not null).Select(s => (s.Stats.Number, Rating: s.Stats.Rating.Ours!.Value)).ToList());

        if (ranked.Count < 2)
        {
            return (null, null);
        }

        var best = ranked.MaxBy(s => s.Rating);
        var worst = ranked.MinBy(s => s.Rating);
        return (new SeasonHighlight(best.Number, best.Rating, source),
            worst.Rating < best.Rating ? new SeasonHighlight(worst.Number, worst.Rating, source) : null);
    }

    /// <summary>Least-squares slope of season ratings against season order.</summary>
    private static string? Trend(List<(SeasonStats Stats, int RatedEpisodes)> seasons)
    {
        var points = seasons
            .Where(s => s.Stats.Rating.External is not null && s.RatedEpisodes >= MinRatedEpisodesPerSeason)
            .Select((s, i) => (X: (double)i, Y: s.Stats.Rating.External!.Value))
            .ToList();
        if (points.Count < 3)
        {
            return null;
        }

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var slope = points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return slope > TrendThreshold ? "rising" : slope < -TrendThreshold ? "falling" : "steady";
    }

    private static RunFacts RunFactsFor(List<SeasonStats> seasons, List<EpisodeCopy> aired)
    {
        var airedSeasons = seasons.Where(s => s.AiredCount > 0).ToList();

        SeasonBreak? longest = null;
        foreach (var (previous, next) in airedSeasons.Zip(airedSeasons.Skip(1)))
        {
            var lastOfPrevious = aired.Where(e => e.SeasonNumber == previous.Number).Max(e => e.AirsAtUtc);
            if (lastOfPrevious is { } end && next.FirstAirsAtUtc is { } start)
            {
                var days = (int)(start - end).TotalDays;
                if (days > (longest?.Days ?? 0))
                {
                    longest = new SeasonBreak(previous.Number, next.Number, days);
                }
            }
        }

        return new RunFacts(
            airedSeasons.Count,
            aired.Count,
            aired.Sum(e => e.RuntimeMinutes ?? 0),
            aired.Min(e => e.AirsAtUtc),
            aired.Max(e => e.AirsAtUtc),
            longest);
    }

    /// <summary>
    /// The next season to start: the earliest season whose first episode is still to air, or an announced
    /// season with a future premiere date and no episodes yet.
    /// </summary>
    private static Premiere? NextPremiere(List<SeasonStats> seasons, IEnumerable<AnnouncedSeason> announced, List<EpisodeCopy> aired, DateTimeOffset now)
    {
        var upcoming = seasons
            .Where(s => s.FirstAirsAtUtc > now)
            .Select(s => (s.Number, PremieresAt: s.FirstAirsAtUtc!.Value))
            .Concat(announced
                .Where(a => a.PremieresAtUtc > now && seasons.All(s => s.Number != a.SeasonNumber))
                .Select(a => (Number: a.SeasonNumber, PremieresAt: a.PremieresAtUtc!.Value)))
            .OrderBy(s => s.PremieresAt)
            .FirstOrDefault();

        if (upcoming == default)
        {
            return null;
        }

        var before = aired.Where(e => e.SeasonNumber < upcoming.Number).ToList();
        var previousEnd = before.Max(e => e.AirsAtUtc);

        return new Premiere(
            upcoming.Number,
            upcoming.PremieresAt,
            previousEnd is { } end ? (int)(upcoming.PremieresAt - end).TotalDays : null,
            before.Count,
            before.Sum(e => e.RuntimeMinutes ?? 0));
    }

    /// <summary>Our users' star ratings of the show (from the minimum number of ratings) and the provider's episode ratings.</summary>
    private static Histograms HistogramsFor(List<EpisodeCopy> episodes, IEnumerable<(int Stars, int Count)> starCounts, DateTimeOffset now)
    {
        var stars = new int[5];
        foreach (var (star, count) in starCounts)
        {
            stars[star - 1] = count;
        }

        // Half-point buckets from the lowest to the highest rating, empty ones included so the shape reads right.
        var counts = episodes
            .Where(e => e.AirsAtUtc <= now && e.ExternalRating is not null)
            .GroupBy(e => Math.Floor(e.ExternalRating!.Value * 2) / 2)
            .ToDictionary(g => g.Key, g => g.Count());
        var buckets = new List<HistogramBucket>();
        if (counts.Count > 0)
        {
            for (var from = counts.Keys.Min(); from <= counts.Keys.Max(); from += 0.5)
            {
                buckets.Add(new HistogramBucket(from, counts.GetValueOrDefault(from)));
            }
        }

        return new Histograms(stars.Sum() >= InsightRules.MinRatingsToShow ? stars : null, buckets);
    }

    /// <summary>Viewers per season, completion and drop-off, from our users' watched marks.</summary>
    private static Viewing? ViewingFor(List<EpisodeCopy> episodes, List<UserEpisodeWatch> watches, DateTimeOffset now)
    {
        var byUser = watches.GroupBy(w => w.UserId).ToList();
        if (byUser.Count < InsightRules.MinUsersToShow)
        {
            return null;
        }

        var aired = episodes.Where(e => e.AirsAtUtc <= now).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        var position = aired.Select((e, i) => (e.EpisodeId, i)).ToDictionary(x => x.EpisodeId, x => x.i);
        var seasonOf = episodes.ToDictionary(e => e.EpisodeId, e => e.SeasonNumber);

        double? completion = aired.Count == 0
            ? null
            : Round((double)byUser.Count(u => u.Count(w => position.ContainsKey(w.EpisodeId)) == aired.Count) / byUser.Count);

        var seasons = watches
            .Where(w => seasonOf.ContainsKey(w.EpisodeId))
            .GroupBy(w => seasonOf[w.EpisodeId])
            .Select(g => new SeasonViewers(g.Key, g.Select(w => w.UserId).Distinct().Count()))
            .Where(s => s.Viewers >= InsightRules.MinUsersToShow)
            .OrderBy(s => s.Number)
            .ToList();

        // A viewer stopped at their furthest aired episode when they haven't watched the show for a while and
        // aired episodes are left; the episode most of them stopped at is the drop-off.
        var cutoff = now.UtcDateTime.AddDays(-InsightRules.StoppedAfterDays);
        var stops = byUser
            .Where(u => u.Max(w => w.WatchedAtUtc) < cutoff)
            .Select(u => u.Where(w => position.ContainsKey(w.EpisodeId)).Select(w => position[w.EpisodeId]).DefaultIfEmpty(-1).Max())
            .Where(i => i >= 0 && i < aired.Count - 1)
            .GroupBy(i => i)
            .Select(g => (Position: g.Key, Stopped: g.Count()))
            .Where(g => g.Stopped >= InsightRules.MinUsersToShow)
            .OrderByDescending(g => g.Stopped)
            .ThenBy(g => g.Position)
            .FirstOrDefault();

        var dropOff = stops == default
            ? null
            : new DropOff(aired[stops.Position].SeasonNumber, aired[stops.Position].EpisodeNumber, aired[stops.Position].Title,
                stops.Stopped, Round((double)stops.Stopped / byUser.Count));

        return new Viewing(byUser.Count, completion, seasons, dropOff);
    }

    /// <summary>Current followers and new follows over the last eight weeks.</summary>
    private static Momentum? MomentumFor(List<DateTime> followedAt, DateTimeOffset now)
    {
        if (followedAt.Count < InsightRules.MinUsersToShow)
        {
            return null;
        }

        var weeks = new int[8];
        foreach (var at in followedAt)
        {
            var weeksAgo = (int)((now.UtcDateTime - at).TotalDays / 7);
            if (weeksAgo is >= 0 and < 8)
            {
                weeks[7 - weeksAgo]++;
            }
        }

        return new Momentum(followedAt.Count, weeks, weeks[4..].Sum(), weeks[..4].Sum());
    }

    /// <summary>
    /// Other shows sharing an actor or a character with this one, in this show's billing order. A shared
    /// character id (the provider lists the same character in both) is flagged, and ranks first.
    /// </summary>
    private static List<SharedCast> SharedCastFor(
        ShowCopy show, IEnumerable<CastCopy> credits, ILookup<string, CastCopy> byPerson, ILookup<string, CastCopy> byCharacter,
        Dictionary<Guid, ShowCopy> shows)
    {
        var found = new List<(SharedCast Shared, int Order, double Popularity)>();

        foreach (var credit in credits.OrderBy(c => c.Order))
        {
            var sameCharacter = credit.CharacterExternalIds.SelectMany(id => byCharacter[id]).Where(o => o.ShowId != show.ShowId);
            var sameActor = byPerson[PersonKey(credit)].Where(o => o.ShowId != show.ShowId);

            foreach (var other in sameCharacter.Concat(sameActor).DistinctBy(o => (o.ShowId, o.PersonName)))
            {
                if (!shows.TryGetValue(other.ShowId, out var otherShow))
                {
                    continue;
                }

                var isSameCharacter = other.CharacterExternalIds.Intersect(credit.CharacterExternalIds).Any();
                found.Add((new SharedCast(credit.PersonName, credit.Character, other.ShowId, otherShow.Title, otherShow.PosterUrl,
                    other.Character, isSameCharacter, credit.PersonId), credit.Order, otherShow.Popularity));
            }
        }

        return found
            .OrderByDescending(f => f.Shared.SameCharacter)
            .ThenBy(f => f.Order)
            .ThenByDescending(f => f.Popularity)
            .DistinctBy(f => (f.Shared.PersonName, f.Shared.OtherShowId))
            .Take(SharedCastPerShow)
            .Select(f => f.Shared)
            .ToList();
    }

    /// <summary>
    /// Who a credit is: the catalog's person id, so two actors who share a name aren't shown as shared cast; the name
    /// for credits stored before ids were sent (the next catalog replay fills those in).
    /// </summary>
    private static string PersonKey(CastCopy credit) => credit.PersonId?.ToString() ?? $"name:{credit.PersonName}";

    /// <summary>
    /// Top episodes overall (genre "") and per genre. The provider gives no vote counts, so raw episode ratings from
    /// a few voters would win. Each episode is scored as its show's overall rating plus how far the episode stands
    /// above that show's typical episode: great episodes of well-regarded shows rank first. Ties go to the more popular show.
    /// </summary>
    private static List<TopEpisodeRow> TopEpisodeRows(List<(ShowCopy Show, EpisodeCopy Episode, (int Sum, int Count) Ours, double Score)> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Show.Popularity)
            .ToList();

        var lists = new[] { (Genre: "", Items: ordered) }
            .Concat(ordered.SelectMany(c => c.Show.Genres).Distinct()
                .Select(g => (Genre: g, Items: ordered.Where(c => c.Show.Genres.Contains(g)).ToList())));

        return lists
            .SelectMany(list => list.Items.Take(TopEpisodesPerList).Select((c, rank) =>
            {
                var ours = Pair(null, c.Ours);
                return new TopEpisodeRow
                {
                    Genre = list.Genre,
                    Rank = rank,
                    EpisodeId = c.Episode.EpisodeId,
                    ShowId = c.Show.ShowId,
                    ShowTitle = c.Show.Title,
                    PosterUrl = c.Show.PosterUrl,
                    SeasonNumber = c.Episode.SeasonNumber,
                    EpisodeNumber = c.Episode.EpisodeNumber,
                    Title = c.Episode.Title,
                    AirsAtUtc = c.Episode.AirsAtUtc,
                    ExternalRating = c.Episode.ExternalRating!.Value,
                    OurRating = ours.Ours,
                    OurRatingCount = ours.OurCount
                };
            }))
            .ToList();
    }

    /// <summary>Each fully aired season's last episode, when it's rated and the season has enough rated episodes.</summary>
    private static IEnumerable<EpisodeCopy> FinalesOf(IReadOnlyList<SeasonStats> seasons, List<EpisodeCopy> episodes)
    {
        var bySeason = episodes.ToLookup(e => e.SeasonNumber);
        return seasons
            .Where(s => s.AiredCount > 0 && s.AiredCount == s.EpisodeCount
                && bySeason[s.Number].Count(e => e.ExternalRating is not null) >= MinRatedEpisodesPerSeason)
            .Select(s => bySeason[s.Number].MaxBy(e => e.EpisodeNumber)!)
            .Where(e => e.ExternalRating is not null);
    }

    private sealed record DiscoveryCandidate(
        ShowCopy Show, int AiredEpisodes, bool WellRated, Consistency? Consistency, int Followers,
        List<(EpisodeCopy Finale, double Score)> Finales);

    /// <summary>The discovery lists (see <see cref="DiscoveryLists"/>), overall (genre "") and per genre.</summary>
    private static List<DiscoveryRow> DiscoveryRows(List<DiscoveryCandidate> candidates)
    {
        var genres = new[] { "" }.Concat(candidates.SelectMany(c => c.Show.Genres).Distinct());
        var rows = new List<DiscoveryRow>();

        var byPopularity = candidates.Select(c => c.Show.Popularity).OrderDescending().ToList();
        var popularCutoff = byPopularity.Count == 0 ? 0 : byPopularity[(int)(byPopularity.Count * HiddenGemPopularShare)];

        foreach (var genre in genres)
        {
            var inGenre = candidates.Where(c => genre == "" || c.Show.Genres.Contains(genre)).ToList();

            Add(DiscoveryLists.HiddenGems, genre, inGenre
                .Where(c => c.AiredEpisodes > 0 && c.Show.ExternalRating >= HiddenGemMinRating
                    && c.Show.Popularity < popularCutoff && c.Followers < InsightRules.MinUsersToShow)
                .OrderByDescending(c => c.Show.ExternalRating)
                .ThenBy(c => c.Show.Popularity)
                .Select(c => Row(c.Show, c.Show.ExternalRating!.Value)));

            Add(DiscoveryLists.MostConsistent, genre, inGenre
                .Where(c => c.WellRated && c.Consistency is not null && c.Show.ExternalRating >= MostConsistentMinRating)
                .OrderBy(c => c.Consistency!.Spread)
                .ThenByDescending(c => c.Show.ExternalRating)
                .Select(c => Row(c.Show, c.Consistency!.Spread)));

            Add(DiscoveryLists.Rollercoasters, genre, inGenre
                .Where(c => c.WellRated && c.Consistency is not null && c.Show.ExternalRating >= RollercoasterMinRating)
                .OrderByDescending(c => c.Consistency!.Spread)
                .Select(c => Row(c.Show, c.Consistency!.Spread)));

            Add(DiscoveryLists.BestFinales, genre, inGenre
                .SelectMany(c => c.Finales.Select(f => (c.Show, f.Finale, f.Score)))
                .OrderByDescending(f => f.Score)
                .ThenByDescending(f => f.Show.Popularity)
                .Select(f => Row(f.Show, f.Finale.ExternalRating!.Value, f.Finale)));
        }

        return rows;

        void Add(string list, string genre, IEnumerable<DiscoveryRow> ranked)
        {
            var rank = 0;
            foreach (var row in ranked.Take(DiscoveryListLength))
            {
                row.List = list;
                row.Genre = genre;
                row.Rank = rank++;
                rows.Add(row);
            }
        }

        static DiscoveryRow Row(ShowCopy show, double value, EpisodeCopy? episode = null) => new()
        {
            List = "",
            Genre = "",
            ShowId = show.ShowId,
            ShowTitle = show.Title,
            PosterUrl = show.PosterUrl,
            ShowExternalRating = show.ExternalRating,
            Value = value,
            EpisodeId = episode?.EpisodeId,
            SeasonNumber = episode?.SeasonNumber,
            EpisodeNumber = episode?.EpisodeNumber,
            EpisodeTitle = episode?.Title
        };
    }

    /// <summary>
    /// Insights for every actor with a catalog id: their shows' ratings, the years those shows span, the actors they
    /// share at least two shows with, and how many of our users follow their shows.
    /// </summary>
    private static List<PersonInsightsRow> PersonInsightsRows(
        List<CastCopy> cast, Dictionary<Guid, ShowCopy> shows, Dictionary<Guid, RunFacts> runs, ILookup<Guid, string> followersByShow,
        DateTime computedAt)
    {
        var credits = cast.Where(c => c.PersonId is not null && shows.ContainsKey(c.ShowId)).ToList();
        var castByShow = credits.ToLookup(c => c.ShowId);

        return credits
            .GroupBy(c => c.PersonId!.Value)
            .Select(person =>
            {
                var personShows = person
                    .DistinctBy(c => c.ShowId)
                    .Select(c => (Show: shows[c.ShowId], c.Character))
                    .OrderByDescending(x => x.Show.Popularity)
                    .Select(x => new PersonShow(x.Show.ShowId, x.Show.Title, x.Show.PosterUrl, x.Show.ExternalRating, x.Character))
                    .ToList();
                var rated = personShows.Where(s => s.Rating is not null).ToList();
                var showIds = personShows.Select(s => s.ShowId).ToHashSet();

                var coStars = personShows
                    .SelectMany(s => castByShow[s.ShowId].Where(c => c.PersonId != person.Key).Select(c => (Credit: c, Show: s)))
                    .GroupBy(x => x.Credit.PersonId!.Value)
                    .Where(g => g.Select(x => x.Show.ShowId).Distinct().Count() >= 2)
                    .OrderByDescending(g => g.Select(x => x.Show.ShowId).Distinct().Count())
                    .ThenBy(g => g.Min(x => x.Credit.Order))
                    .Take(CoStarsPerPerson)
                    .Select(g => new CoStar(g.Key, g.First().Credit.PersonName,
                        g.DistinctBy(x => x.Show.ShowId).Select(x => x.Show with { Character = x.Credit.Character }).ToList()))
                    .ToList();

                var followers = showIds.SelectMany(id => followersByShow[id]).Distinct().Count();
                var years = showIds.Select(id => runs.GetValueOrDefault(id)).OfType<RunFacts>().ToList();

                var insights = new PersonInsights(
                    person.Key,
                    person.First().PersonName,
                    computedAt,
                    personShows.Count,
                    rated.Count > 0 ? Round(rated.Average(s => s.Rating!.Value)) : null,
                    rated.MaxBy(s => s.Rating),
                    years.Min(r => r.FirstAiredUtc)?.Year,
                    years.Max(r => r.LastAiredUtc)?.Year,
                    coStars,
                    followers >= InsightRules.MinUsersToShow ? followers : null);

                return new PersonInsightsRow
                {
                    PersonId = person.Key,
                    Document = JsonSerializer.Serialize(insights, JsonOptions),
                    ComputedAtUtc = computedAt
                };
            })
            .ToList();
    }

    private Task ReplaceListsAsync(
        List<PremiereRow> premieres, List<TopEpisodeRow> topEpisodes, List<DiscoveryRow> discovery, List<PersonInsightsRow> people,
        CancellationToken ct) =>
        // One transaction, so readers never see an empty list mid-refresh. Run through the execution strategy:
        // Aspire's Npgsql integration retries on transient errors and refuses transactions started outside it.
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Premieres.ExecuteDeleteAsync(ct);
            await db.TopEpisodes.ExecuteDeleteAsync(ct);
            await db.Discovery.ExecuteDeleteAsync(ct);
            await db.PersonInsights.ExecuteDeleteAsync(ct);
            db.Premieres.AddRange(premieres);
            db.TopEpisodes.AddRange(topEpisodes);
            db.Discovery.AddRange(discovery);
            db.PersonInsights.AddRange(people);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            db.ChangeTracker.Clear();
        });

    /// <summary>
    /// An episode's ratings, or none when there are too few to show. Applied before any aggregation, so a hidden
    /// episode's ratings never feed a season average either (else subtracting the visible episodes would reveal them).
    /// </summary>
    private static (int Sum, int Count) Visible((int Sum, int Count) ratings) =>
        ratings.Count >= InsightRules.MinRatingsToShow ? ratings : (0, 0);

    private static IEnumerable<EpisodeCopy> Aired(List<EpisodeCopy> episodes, DateTimeOffset now) => episodes.Where(e => e.AirsAtUtc <= now);

    private static RatingPair Pair(double? external, (int Sum, int Count) ours) =>
        new(external, ours.Count >= InsightRules.MinRatingsToShow ? Average(ours) : null, ours.Count);

    private static double? Average((int Sum, int Count) ratings) =>
        ratings.Count == 0 ? null : Round((double)ratings.Sum / ratings.Count);

    private static double Round(double value) => Math.Round(value, 2);
}

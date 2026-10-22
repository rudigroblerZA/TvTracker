using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>Viewers per season, completion and drop-off, from our users' watched marks; none below the minimum number of viewers.</summary>
public sealed class ViewingInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var watches = show.Watches;
        var byUser = watches.GroupBy(w => w.UserId).ToList();
        if (byUser.Count < InsightRules.MinUsersToShow)
        {
            return insights with { Viewing = null };
        }

        var aired = show.Aired.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
        var position = aired.Select((e, i) => (e.EpisodeId, i)).ToDictionary(x => x.EpisodeId, x => x.i);
        var seasonOf = show.Episodes.ToDictionary(e => e.EpisodeId, e => e.SeasonNumber);

        double? completion = aired.Count == 0
            ? null
            : InsightMath.Round((double)byUser.Count(u => u.Count(w => position.ContainsKey(w.EpisodeId)) == aired.Count) / byUser.Count);

        var seasons = watches
            .Where(w => seasonOf.ContainsKey(w.EpisodeId))
            .GroupBy(w => seasonOf[w.EpisodeId])
            .Select(g => new SeasonViewers(g.Key, g.Select(w => w.UserId).Distinct().Count()))
            .Where(s => s.Viewers >= InsightRules.MinUsersToShow)
            .OrderBy(s => s.Number)
            .ToList();

        // A viewer stopped at their furthest aired episode when they haven't watched the show for a while and
        // aired episodes are left; the episode most of them stopped at is the drop-off.
        var cutoff = show.Now.UtcDateTime.AddDays(-InsightRules.StoppedAfterDays);
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
                stops.Stopped, InsightMath.Round((double)stops.Stopped / byUser.Count));

        return insights with { Viewing = new Viewing(byUser.Count, completion, seasons, dropOff) };
    }
}

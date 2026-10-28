using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// How a season that just finished did (<see cref="SeasonVerdict"/>): the latest regular season with every listed
/// episode aired, from <see cref="From"/> after its finale (so the provider's episode ratings come in, and viewers catch
/// up before "finished" is counted) to <see cref="Until"/>.
/// <para>
/// <b>Is it over?</b> TVmaze lists a weekly show's next episode only a few days ahead, so "every listed episode aired"
/// also describes Cops in week 17 of a running season. A season counts as over when the show has ended, a later season
/// is listed or announced, or the season dropped at once; otherwise once it has been quiet for twice its usual gap
/// between episodes (at least <see cref="MinQuiet"/>): 14 days for a weekly show. On 28 October 2026 that gave 24
/// verdicts, among them Ted Lasso (weekly, quiet for 21 days) and the streaming drops (The Terminal List, The
/// Diplomat), and held back Cops, Slow Horses and All Creatures Great and Small (weekly, last aired a week ago) until
/// they've been quiet long enough. A reality show back from a long mid-season break can still be called over early.
/// </para>
/// </summary>
public sealed class SeasonVerdictInsight : IShowInsight
{
    public static readonly TimeSpan From = TimeSpan.FromDays(3);
    public static readonly TimeSpan Until = TimeSpan.FromDays(45);
    private static readonly TimeSpan MinQuiet = TimeSpan.FromDays(7);

    /// <summary>Seasons longer than this are daily shows' (talk, news, soaps), whose "season" is a year of episodes.</summary>
    private const int MaxEpisodes = 60;

    /// <summary>A season this far above or below the others' median, or a finale this far from its season, is called so.</summary>
    private const double Margin = 0.3;

    /// <summary>"Best yet" and "weakest yet" need this many other seasons; against one, it's only above or below.</summary>
    private const int MinComparedForRecord = 2;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights) =>
        insights with { SeasonVerdict = VerdictFor(show) };

    private static SeasonVerdict? VerdictFor(ShowInsightContext show)
    {
        var finished = show.Seasons
            .Where(s => s.Stats.Number > 0 && s.Stats.EpisodeCount <= MaxEpisodes && s.Stats.AiredCount == s.Stats.EpisodeCount)
            .Where(s => s.Stats.LastAirsAtUtc is { } last && show.Now - last >= From && show.Now - last <= Until)
            .OrderByDescending(s => s.Stats.Number)
            .FirstOrDefault();
        // A later season already under way makes this one old news.
        if (finished is null || !finished.RatingCounts || finished.Stats.Rating.External is not { } rating
            || show.Aired.Any(e => e.SeasonNumber > finished.Stats.Number)
            || ReadyAt(show, finished.Stats.Number, finished.Stats.LastAirsAtUtc!.Value) is not { } readyAt || readyAt > show.Now)
        {
            return null;
        }

        var number = finished.Stats.Number;
        var others = show.RatedSeasons.Where(s => s.Number > 0 && s.Number != number).Select(s => s.Rating).ToList();
        var othersMedian = others.Count > 0 ? Median(others) : (double?)null;
        var higher = others.Count(r => r > rating);
        var lower = others.Count(r => r < rating);
        var verdict = othersMedian is not { } median ? SeasonVerdicts.FirstSeason
            : others.Count >= MinComparedForRecord && higher == 0 && lower == others.Count ? SeasonVerdicts.BestYet
            : others.Count >= MinComparedForRecord && lower == 0 && higher == others.Count ? SeasonVerdicts.WeakestYet
            : Reaches(rating - median, Margin) ? SeasonVerdicts.Above
            : Reaches(median - rating, Margin) ? SeasonVerdicts.Below
            : SeasonVerdicts.InLine;

        // The finale is the season's last episode to air; episodes that aired together (a streaming drop) go by number.
        var episodes = show.Aired.Where(e => e.SeasonNumber == number).OrderBy(e => e.AirsAtUtc).ThenBy(e => e.EpisodeNumber).ToList();
        var finale = episodes[^1];
        var finaleVerdict = finale.ExternalRating is not { } finaleRating ? null
            : Reaches(finaleRating - rating, Margin) ? FinaleVerdicts.Strong
            : Reaches(rating - finaleRating, Margin) ? FinaleVerdicts.Weak
            : FinaleVerdicts.InLine;

        var seasonEpisodes = episodes.Select(e => e.EpisodeId).ToHashSet();
        var started = show.Watches.Where(w => seasonEpisodes.Contains(w.EpisodeId)).Select(w => w.UserId).Distinct().Count();
        var finishedViewers = show.Watches.Where(w => w.EpisodeId == finale.EpisodeId).Select(w => w.UserId).Distinct().Count();
        var enough = started >= InsightRules.MinUsersToShow;

        return new SeasonVerdict(number, finished.Stats.EpisodeCount, finished.Stats.LastAirsAtUtc!.Value.UtcDateTime, rating,
            finished.RatedEpisodes, verdict, othersMedian is { } m ? Round(m) : null, others.Count, higher, lower,
            finale.ExternalRating, finaleVerdict, enough ? started : null, enough ? finishedViewers : null, readyAt.UtcDateTime);
    }

    /// <summary>
    /// When the season can be called over (see the class remarks): <see cref="From"/> after the finale when something
    /// says so, else once it has been quiet long enough. Null when it can't be told (a season of one episode, with no
    /// other signal).
    /// </summary>
    private static DateTimeOffset? ReadyAt(ShowInsightContext show, int number, DateTimeOffset finale)
    {
        var airTimes = show.Aired.Where(e => e.SeasonNumber == number).Select(e => e.AirsAtUtc!.Value).Order().ToList();
        var dropped = airTimes.Count > 1 && airTimes[^1] - airTimes[0] < TimeSpan.FromDays(1);
        var said = show.Show.Status is ShowStatus.Ended or ShowStatus.Cancelled
            || show.Episodes.Any(e => e.SeasonNumber > number)
            || show.Sources.AnnouncedSeasons[show.ShowId].Any(a => a.SeasonNumber > number)
            || dropped;
        if (said)
        {
            return finale + From;
        }
        if (airTimes.Count < 2)
        {
            return null;
        }

        var gaps = airTimes.Zip(airTimes.Skip(1), (a, b) => (b - a).TotalDays).Where(d => d >= 0.5).ToList();
        var usual = gaps.Count > 0 ? TimeSpan.FromDays(Median(gaps)) : TimeSpan.Zero;
        var quiet = usual * 2 > MinQuiet ? usual * 2 : MinQuiet;
        return finale + (quiet > From ? quiet : From);
    }
}

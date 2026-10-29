using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// How a season that just finished did (<see cref="SeasonVerdict"/>): the latest regular season with every listed
/// episode aired, from <see cref="SeasonVerdict.From"/> after its finale (so the provider's episode ratings come in, and
/// viewers catch up before "finished" is counted) to <see cref="SeasonVerdict.Until"/>.
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
            .Where(s => s.Stats.LastAirsAtUtc is { } last && show.Now - last >= SeasonVerdict.From && show.Now - last <= SeasonVerdict.Until)
            .OrderByDescending(s => s.Stats.Number)
            .FirstOrDefault();
        // A later season already under way makes this one old news.
        if (finished is null || !finished.RatingCounts || finished.Stats.Rating.External is not { } rating
            || show.Aired.Any(e => e.SeasonNumber > finished.Stats.Number))
        {
            return null;
        }

        // In air order: the finale is the last to air; episodes that aired together (a streaming drop) go by number.
        var number = finished.Stats.Number;
        var episodes = show.Aired.Where(e => e.SeasonNumber == number).OrderBy(e => e.AirsAtUtc).ThenBy(e => e.EpisodeNumber).ToList();
        var finale = episodes[^1];
        if (ReadyAt(show, episodes) is not { } readyAt || readyAt > show.Now)
        {
            return null;
        }

        var others = show.RatedSeasons.Where(s => s.Number > 0 && s.Number != number).Select(s => s.Rating).ToList();
        var othersMedian = others.Count > 0 ? Median(others) : (double?)null;
        var higher = others.Count(r => r > rating);
        var lower = others.Count(r => r < rating);
        var canRecord = others.Count >= MinComparedForRecord;
        // Nothing to compare: the first season, or a later one whose earlier seasons have too few rated episodes.
        var verdict = othersMedian is not { } median
                ? show.Aired.Any(e => e.SeasonNumber > 0 && e.SeasonNumber < number) ? SeasonVerdicts.Unranked : SeasonVerdicts.FirstSeason
            : canRecord && lower == others.Count ? SeasonVerdicts.BestYet
            : canRecord && higher == others.Count ? SeasonVerdicts.WeakestYet
            : Reaches(rating - median, Margin) ? SeasonVerdicts.Above
            : Reaches(median - rating, Margin) ? SeasonVerdicts.Below
            : SeasonVerdicts.InLine;

        var finaleVerdict = finale.ExternalRating is not { } finaleRating ? null
            : Reaches(finaleRating - rating, Margin) ? FinaleVerdicts.Strong
            : Reaches(rating - finaleRating, Margin) ? FinaleVerdicts.Weak
            : FinaleVerdicts.InLine;

        var seasonEpisodes = episodes.Select(e => e.EpisodeId).ToHashSet();
        var started = show.Watches.Where(w => seasonEpisodes.Contains(w.EpisodeId)).Select(w => w.UserId).Distinct().Count();
        var finishedViewers = show.Watches.Where(w => w.EpisodeId == finale.EpisodeId).Select(w => w.UserId).Distinct().Count();
        var enough = started >= InsightRules.MinUsersToShow;

        return new SeasonVerdict(number, finished.Stats.EpisodeCount, finale.AirsAtUtc!.Value.UtcDateTime, rating,
            finished.RatedEpisodes, verdict, othersMedian is { } m ? Round(m) : null, others.Count, higher, lower,
            finale.ExternalRating, finaleVerdict, enough ? started : null, enough ? finishedViewers : null, readyAt.UtcDateTime);
    }

    /// <summary>
    /// When the season (its aired <paramref name="episodes"/>, in air order) can be called over (see the class
    /// remarks): <see cref="SeasonVerdict.From"/> after the finale when something says so, else once it has been quiet
    /// long enough. Null when it can't be told (a season of one episode, with no other signal).
    /// </summary>
    private static DateTimeOffset? ReadyAt(ShowInsightContext show, List<EpisodeCopy> episodes)
    {
        var number = episodes[0].SeasonNumber;
        var finale = episodes[^1].AirsAtUtc!.Value;
        var said = show.Show.HasEnded()
            || show.Episodes.Any(e => e.SeasonNumber > number)
            || show.Sources.AnnouncedSeasons[show.ShowId].Any(a => a.SeasonNumber > number)
            || episodes.Count > 1 && show.ReleaseDays(number).Count == 1;
        if (said)
        {
            return finale + SeasonVerdict.From;
        }
        if (episodes.Count < 2)
        {
            return null;
        }

        var gaps = episodes.Zip(episodes.Skip(1), (a, b) => (b.AirsAtUtc!.Value - a.AirsAtUtc!.Value).TotalDays).Where(d => d >= 0.5).ToList();
        var usual = gaps.Count > 0 ? TimeSpan.FromDays(Median(gaps)) : TimeSpan.Zero;
        // Never under MinQuiet, which is longer than From.
        return finale + (usual * 2 > MinQuiet ? usual * 2 : MinQuiet);
    }
}

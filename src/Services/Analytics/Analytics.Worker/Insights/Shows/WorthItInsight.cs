using Analytics.Data;
using TvTracker.IntegrationEvents;

using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// Is it worth your time (<see cref="WorthIt"/>)? The provider's rating, moved by the earlier stages' findings about how
/// the hours go: a slow start, a decline (by the share of the running time it covers), the ending and the spread of
/// episode ratings. Runs after <see cref="LandingInsight"/>, since it reads what the stages before it set.
/// <para>
/// Checked against the catalog when it was added: Breaking Bad scores 100 and Better Call Saul 97; Game of Thrones 66 and
/// Dexter 56, held back by their declines and endings; How I Met Your Mother 22, with a slow start, a decline over more
/// than half of its running time and an ending that fizzles.
/// </para>
/// </summary>
public sealed class WorthItInsight : IShowInsight
{
    /// <summary>Without this many rated episodes, the rating says too little about the hours to score them.</summary>
    private const int MinRatedEpisodes = 5;

    /// <summary>A turn this far in costs <see cref="SlowStartPoints"/>; past <see cref="VerySlowStartMinutes"/>, twice that.</summary>
    private const int SlowStartMinutes = 180;

    private const int VerySlowStartMinutes = 600;
    private const double SlowStartPoints = -0.15;
    private const double GoodStartPoints = 0.1;

    /// <summary>A decline costs this times the share of the running time from the season it sets in.</summary>
    private const double DeclinePoints = -0.6;

    private const double RollercoasterPoints = -0.15;
    private const double SteadyPoints = 0.1;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights) =>
        insights with { WorthIt = WorthItFor(show, insights) };

    private static WorthIt? WorthItFor(ShowInsightContext show, ShowInsights insights)
    {
        if (show.Show.ExternalRating is not { } rating || show.RatedAired.Count < MinRatedEpisodes)
        {
            return null;
        }

        var factors = new List<WorthItFactor>();

        if (insights.GetsGood is { Turn: { MinutesBefore: >= SlowStartMinutes } turn })
        {
            factors.Add(new(WorthItFactors.Start, turn.MinutesBefore >= VerySlowStartMinutes ? 2 * SlowStartPoints : SlowStartPoints,
                $"Slow start: gets good at S{turn.SeasonNumber:00}E{turn.EpisodeNumber:00}, {Hours(turn.MinutesBefore)} in"));
        }
        else if (insights.GetsGood is { FromTheStart: true })
        {
            factors.Add(new(WorthItFactors.Start, GoodStartPoints, "Good from the first episode"));
        }

        // Regular seasons only, so specials don't dilute the share.
        if (insights.Patterns?.Arc is { DeclineFromSeason: { } declineFrom })
        {
            var seasons = show.SeasonStats.Where(s => s.Number > 0).ToList();
            var total = seasons.Sum(s => s.RuntimeMinutes);
            var after = seasons.Where(s => s.Number >= declineFrom).Sum(s => s.RuntimeMinutes);
            if (total > 0 && after > 0)
            {
                var share = (double)after / total;
                factors.Add(new(WorthItFactors.Decline, InsightMath.Round(DeclinePoints * share),
                    $"Weaker from season {declineFrom}: {share * 100:0}% of its running time"));
            }
        }

        if (insights.Landing is { } landing && Ending(landing.Verdict) is { } ending)
        {
            factors.Add(new(WorthItFactors.Ending, ending.Points, ending.Text));
        }

        switch (insights.Patterns?.Consistency?.Label)
        {
            case "rollercoaster":
                factors.Add(new(WorthItFactors.Consistency, RollercoasterPoints, "Uneven: its episodes vary a lot"));
                break;
            case "steady":
                factors.Add(new(WorthItFactors.Consistency, SteadyPoints, "Steady: its episodes rarely dip"));
                break;
        }

        var value = InsightMath.Round(rating + factors.Sum(f => f.Points));
        var score = WorthItScale.Score(value);
        return new WorthIt(score, WorthItScale.Verdict(score), rating, value, factors, insights.Run.RuntimeMinutes,
            !show.Show.HasEnded());
    }

    /// <summary>The ending's points, as the backlog and time off weigh it (Analytics.API's ShowValue).</summary>
    private static (double Points, string Text)? Ending(string verdict) => verdict switch
    {
        LandingVerdicts.Sticks => (0.3, "Sticks the landing"),
        LandingVerdicts.GreatFinale => (0.3, "Great finale"),
        LandingVerdicts.WeakerLastSeason => (-0.1, "Weaker last season"),
        LandingVerdicts.FinaleLetsDown => (-0.3, "The finale lets it down"),
        LandingVerdicts.Fizzles => (-0.5, "Fizzles out at the end"),
        _ => null
    };

}

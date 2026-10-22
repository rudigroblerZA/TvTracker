using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>How long the show has run: seasons, episodes, hours, first and last air dates, and its longest break.</summary>
public sealed class RunInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var aired = show.Aired;
        var airedSeasons = show.SeasonStats.Where(s => s.AiredCount > 0).ToList();

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

        return insights with
        {
            Run = new RunFacts(
                airedSeasons.Count,
                aired.Count,
                aired.Sum(e => e.RuntimeMinutes ?? 0),
                aired.Min(e => e.AirsAtUtc),
                aired.Max(e => e.AirsAtUtc),
                longest)
        };
    }
}

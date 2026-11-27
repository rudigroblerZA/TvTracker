
namespace TvTracker.InsightsEngine;

/// <summary>
/// The next season to start: the earliest season whose first episode is still to air, or an announced season with a
/// future premiere date and no episodes yet. Comes with what to watch first to be ready. Regular seasons only: a special
/// still to air isn't a premiere, and specials aren't part of catching up.
/// </summary>
public sealed class NextPremiereInsight : IShowInsight
{
    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var seasons = show.RegularSeasonStats;
        var upcoming = seasons
            .Where(s => s.FirstAirsAtUtc > show.Now)
            .Select(s => (s.Number, PremieresAt: s.FirstAirsAtUtc!.Value))
            .Concat(show.Sources.AnnouncedSeasons[show.ShowId]
                .Where(a => a.PremieresAtUtc > show.Now && seasons.All(s => s.Number != a.SeasonNumber))
                .Select(a => (Number: a.SeasonNumber, PremieresAt: a.PremieresAtUtc!.Value)))
            .OrderBy(s => s.PremieresAt)
            .FirstOrDefault();

        if (upcoming == default)
        {
            return insights with { NextPremiere = null };
        }

        var before = show.RegularAired.Where(e => e.SeasonNumber < upcoming.Number).ToList();
        var previousEnd = before.Max(e => e.AirsAtUtc);

        return insights with
        {
            NextPremiere = new Premiere(
                upcoming.Number,
                upcoming.PremieresAt,
                previousEnd is { } end ? (int)(upcoming.PremieresAt - end).TotalDays : null,
                before.Count,
                before.Sum(e => e.RuntimeMinutes ?? 0))
        };
    }
}

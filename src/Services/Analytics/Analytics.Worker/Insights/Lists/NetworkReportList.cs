using System.Text.Json;
using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Network report cards (<see cref="NetworkReport"/>): for each network or streaming service with at least
/// <see cref="NetworkReports.MinShows"/> catalog shows, how its shows rate, end, come back and how long they keep viewers
/// waiting, plus the same for the whole catalog as a baseline. All from the provider's data and the shows' documents,
/// so no figure is about our users.
/// <list type="bullet">
/// <item><b>Endings:</b> the shows' <see cref="Landing"/> verdicts, so only ended shows whose final season can be judged.</item>
/// <item><b>Renewals:</b> a season counts once its fate is known: every season of an ended show, and every season of a
/// running show that a later one (listed or announced) follows. A running show's latest season is still open. "Over
/// after one season" was tried and dropped: TVmaze has no "cancelled", so every ended one-season show reads as a limited
/// series, and leaving those out left none.</item>
/// <item><b>Waits:</b> from a season's last aired episode to the next season's first, over seasons that both aired.</item>
/// </list>
/// </summary>
public sealed class NetworkReportList : IListInsight
{
    private const int MaxShows = 3;

    public ListBuilder Begin(InsightSources sources) => new Builder(sources);

    private sealed record ShowFacts(ShowCopy Show, Landing? Landing, bool DropsAtOnce, int Decided, int Renewed, List<double> Waits);

    private sealed class Builder(InsightSources sources) : ListBuilder<NetworkReportRow>(db => db.NetworkReports)
    {
        private readonly List<ShowFacts> shows = [];

        public override string Name => "networks";

        public override void Add(ShowInsightContext show, ShowInsights insights)
        {
            var seasons = sources.SeasonHistory[show.ShowId].ToList();
            var announced = sources.AnnouncedSeasons[show.ShowId].Select(a => a.SeasonNumber).ToList();
            var aired = seasons.Where(s => s.Aired > 0).OrderBy(s => s.Number).ToList();
            var latest = seasons.Select(s => s.Number).Concat(announced).DefaultIfEmpty(0).Max();
            var followed = aired.Count(s => s.Number < latest);
            var ended = show.Show.Status is TvTracker.IntegrationEvents.ShowStatus.Ended or TvTracker.IntegrationEvents.ShowStatus.Cancelled;

            var waits = aired.Zip(aired.Skip(1))
                .Where(p => p.First.LastAired is not null && p.Second.First is not null)
                .Select(p => (p.Second.First!.Value - p.First.LastAired!.Value).TotalDays)
                .Where(d => d > 0)
                .ToList();
            var tags = insights.Tags ?? [];
            shows.Add(new ShowFacts(show.Show, insights.Landing, tags.Any(t => t.Key == ShowTags.DropsAtOnce),
                ended ? aired.Count : followed, followed, waits));
        }

        protected override IReadOnlyCollection<NetworkReportRow> Rows()
        {
            var groups = shows
                .Where(s => !string.IsNullOrWhiteSpace(s.Show.Network))
                .GroupBy(s => s.Show.Network!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= NetworkReports.MinShows)
                .Select(g => (Name: g.First().Show.Network!.Trim(), Shows: g.ToList()))
                .Append((Name: NetworkReports.Catalog, Shows: shows));

            return groups.Select(g =>
                {
                    var report = ReportOf(g.Name, g.Shows);
                    return new NetworkReportRow
                    {
                        Network = g.Name,
                        Shows = report.Shows,
                        Document = JsonSerializer.Serialize(report, InsightsPipeline.JsonOptions),
                        ComputedAtUtc = sources.ComputedAtUtc
                    };
                })
                .ToList();
        }

        private NetworkReport ReportOf(string name, List<ShowFacts> facts)
        {
            var rated = facts.Where(f => f.Show.ExternalRating is not null).ToList();
            var ended = facts.Where(f => f.Show.Status is TvTracker.IntegrationEvents.ShowStatus.Ended
                or TvTracker.IntegrationEvents.ShowStatus.Cancelled).ToList();

            var judged = facts.Where(f => f.Landing is not null).Select(f => f.Landing!.Verdict).ToList();
            var endings = judged.Count < NetworkReports.MinJudged ? null : new NetworkEndings(judged.Count,
                judged.Count(v => v is LandingVerdicts.Sticks or LandingVerdicts.GreatFinale),
                judged.Count(v => v == LandingVerdicts.AsItRan),
                judged.Count(v => v is LandingVerdicts.WeakerLastSeason or LandingVerdicts.FinaleLetsDown),
                judged.Count(v => v == LandingVerdicts.Fizzles));

            var decided = facts.Sum(f => f.Decided);
            var renewals = decided < NetworkReports.MinJudged ? null : new NetworkRenewals(decided, facts.Sum(f => f.Renewed));

            var waits = facts.SelectMany(f => f.Waits).ToList();
            return new NetworkReport(
                name,
                sources.ComputedAtUtc,
                facts.Count,
                ended.Count,
                rated.Count > 0 ? InsightMath.Round(rated.Average(f => f.Show.ExternalRating!.Value)) : null,
                endings,
                renewals,
                waits.Count >= NetworkReports.MinJudged ? (int)Math.Round(InsightMath.Median(waits)) : null,
                waits.Count,
                facts.Count > 0 ? InsightMath.Round((double)facts.Count(f => f.DropsAtOnce) / facts.Count) : 0,
                name == NetworkReports.Catalog ? [] : rated
                    .OrderByDescending(f => f.Show.ExternalRating).ThenByDescending(f => f.Show.Popularity)
                    .Take(MaxShows)
                    .Select(f => new NetworkShow(f.Show.ShowId, f.Show.Title, f.Show.PosterUrl, f.Show.ExternalRating))
                    .ToList(),
                name == NetworkReports.Catalog ? [] : facts
                    .Where(f => f.Landing?.Verdict is LandingVerdicts.Sticks or LandingVerdicts.GreatFinale)
                    .OrderByDescending(f => f.Landing!.FinaleRating)
                    .Take(MaxShows)
                    .Select(f => new NetworkShow(f.Show.ShowId, f.Show.Title, f.Show.PosterUrl, f.Landing!.FinaleRating))
                    .ToList());
        }
    }
}

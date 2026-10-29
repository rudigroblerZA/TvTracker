using System.Text.Json;
using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Insights for every actor with a catalog id: their shows' ratings, the years those shows span, the actors they
/// share at least two shows with, and how many of our users follow their shows.
/// </summary>
public sealed class PersonInsightsList : IListInsight
{
    private const int CoStarsPerPerson = 6;

    public ListBuilder Begin(InsightSources sources) => new Builder(sources);

    private sealed class Builder(InsightSources sources) : ListBuilder<PersonInsightsRow>(db => db.PersonInsights)
    {
        private readonly Dictionary<Guid, RunFacts> runs = [];

        public override string Name => "people";

        public override void Add(ShowInsightContext show, ShowInsights insights) => runs[show.ShowId] = insights.Run;

        protected override IReadOnlyCollection<PersonInsightsRow> Rows()
        {
            var shows = sources.Shows;
            var credits = sources.Cast.Where(c => c.PersonId is not null && shows.ContainsKey(c.ShowId)).ToList();
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

                    var followers = showIds.SelectMany(id => sources.FollowersByShow[id]).Distinct().Count();
                    var years = showIds.Select(id => runs.GetValueOrDefault(id)).OfType<RunFacts>().ToList();

                    var insights = new PersonInsights(
                        person.Key,
                        person.First().PersonName,
                        sources.ComputedAtUtc,
                        personShows.Count,
                        rated.Count > 0 ? InsightMath.Round(rated.Average(s => s.Rating!.Value)) : null,
                        rated.MaxBy(s => s.Rating),
                        years.Min(r => r.FirstAiredUtc)?.Year,
                        years.Max(r => r.LastAiredUtc)?.Year,
                        coStars,
                        followers >= InsightRules.MinUsersToShow ? followers : null);

                    return new PersonInsightsRow
                    {
                        PersonId = person.Key,
                        Document = InsightDocuments.Serialize(insights),
                        ComputedAtUtc = sources.ComputedAtUtc
                    };
                })
                .ToList();
        }
    }
}

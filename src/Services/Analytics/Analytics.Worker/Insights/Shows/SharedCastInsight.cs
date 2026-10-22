using Analytics.Data;

namespace Analytics.Worker.Insights;

/// <summary>
/// Other shows sharing an actor or a character with this one, in this show's billing order. A shared character id
/// (the provider lists the same character in both) is flagged, and ranks first.
/// </summary>
public sealed class SharedCastInsight : IShowInsight
{
    private const int SharedCastPerShow = 12;

    public ShowInsights Apply(ShowInsightContext show, ShowInsights insights)
    {
        var sources = show.Sources;
        var found = new List<(SharedCast Shared, int Order, double Popularity)>();

        foreach (var credit in sources.CastByShow[show.ShowId].OrderBy(c => c.Order))
        {
            var sameCharacter = credit.CharacterExternalIds.SelectMany(id => sources.CastByCharacter[id]).Where(o => o.ShowId != show.ShowId);
            var sameActor = sources.CastByPerson[InsightSources.PersonKey(credit)].Where(o => o.ShowId != show.ShowId);

            foreach (var other in sameCharacter.Concat(sameActor).DistinctBy(o => (o.ShowId, o.PersonName)))
            {
                if (!sources.Shows.TryGetValue(other.ShowId, out var otherShow))
                {
                    continue;
                }

                var isSameCharacter = other.CharacterExternalIds.Intersect(credit.CharacterExternalIds).Any();
                found.Add((new SharedCast(credit.PersonName, credit.Character, other.ShowId, otherShow.Title, otherShow.PosterUrl,
                    other.Character, isSameCharacter, credit.PersonId), credit.Order, otherShow.Popularity));
            }
        }

        return insights with
        {
            SharedCast = found
                .OrderByDescending(f => f.Shared.SameCharacter)
                .ThenBy(f => f.Order)
                .ThenByDescending(f => f.Popularity)
                .DistinctBy(f => (f.Shared.PersonName, f.Shared.OtherShowId))
                .Take(SharedCastPerShow)
                .Select(f => f.Shared)
                .ToList()
        };
    }
}

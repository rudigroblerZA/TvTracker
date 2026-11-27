namespace TvTracker.InsightsEngine;

/// <param name="RatedShows">How many shows the caller has rated.</param>
/// <param name="Twins">How many people's ratings track theirs; 0 under <see cref="InsightRules.MinUsersToShow"/>, so a
/// smaller count is never shown.</param>
/// <param name="Agreement">How closely, on average (0-1: 1 is the same stars on every shared show).</param>
/// <param name="Reason">Why there are no picks, when there aren't.</param>
/// <param name="AgreeOn">Shows the caller and several twins both rated highly: what the twins have in common with them.</param>
/// <param name="Picks">Shows several twins loved that the caller hasn't rated, followed or watched.</param>
public record TasteTwinsDto(int RatedShows, int Twins, double? Agreement, string? Reason, IReadOnlyList<TwinShowDto> AgreeOn,
    IReadOnlyList<TwinPickDto> Picks);

public record TwinShowDto(Guid ShowId, string Title, string? PosterUrl);

/// <param name="Loved">How many of the twins rated it 4 or 5 stars (at least <see cref="InsightRules.MinUsersToShow"/>).</param>
/// <param name="Stars">Their average stars for it.</param>
public record TwinPickDto(Guid ShowId, string Title, string? PosterUrl, int Loved, double Stars, double? Rating);

/// <summary>
/// Taste twins: the people whose show ratings track the caller's, and what they loved that the caller hasn't seen.
/// Computed on request from the ratings copy; nobody is named or counted alone.
/// <list type="bullet">
/// <item><b>Agreement:</b> 1 − mean |stars difference| / 4 over the shows both rated, with at least
/// <see cref="MinShared"/> in common, shrunk by n / (n + 3) so a perfect match on 3 shows doesn't beat a close one on 15.
/// The best <see cref="MaxTwins"/> with agreement of at least <see cref="MinAgreement"/> are the twins.</item>
/// <item><b>Privacy (the 5-user rule):</b> nothing is shown, not even the count, with fewer than
/// <see cref="InsightRules.MinUsersToShow"/> twins, and a pick or a shared favourite needs that many twins behind it too,
/// since its "loved by" count and average stars are figures about them.</item>
/// <item><b>In SQL:</b> agreement is computed in the database (a join on the caller's ratings, grouped by user), so only
/// candidate twins come back, not every rating of every show the caller rated.</item>
/// <item><b>Picks:</b> scored by the twins who rated it 4+, each weighted by their agreement and how much they liked it
/// (5 stars twice 4), and left out when the caller has rated, followed or watched it.</item>
/// </list>
/// </summary>
public static class TasteTwins
{
    public const int MinRated = 5;

    public const int MinShared = 3;

    public const double MinAgreement = 0.75;

    public const int MaxTwins = 25;

    private const int MaxPicks = 20;

    private const int MaxAgreeOn = 6;

    /// <summary>
    /// Stars from which a show counts as loved.
    /// </summary>
    internal const int LovedStars = 4;

    /// <param name="Agreement">1 less the average difference in stars over the shows both rated, as a share of the scale.</param>
    internal sealed record Twin(string UserId, int Shared, double Agreement);

    /// <summary>
    /// A twin's loved show.
    /// </summary>
    internal sealed record Loved(string UserId, Guid ShowId, int Stars);

    internal sealed record Pick(Guid ShowId, int Loved, double Stars);

    /// <summary>
    /// The answer for a viewer who hasn't rated <see cref="MinRated"/> shows yet.
    /// </summary>
    internal static TasteTwinsDto TooFewRated(int rated) =>
        Empty(rated, 0, null, $"Rate {MinRated - rated} more {(MinRated - rated == 1 ? "show" : "shows")} to find your taste twins.");

    /// <summary>
    /// The answer when fewer than <see cref="InsightRules.MinUsersToShow"/> people agree enough.
    /// </summary>
    internal static TasteTwinsDto TooFewTwins(int rated) =>
        Empty(rated, 0, null, "Not enough people here share your taste yet. Rating more shows helps.");

    /// <summary>
    /// The viewer's twins among the people who share <see cref="MinShared"/> or more rated shows: those agreeing at
    /// least <see cref="MinAgreement"/>, the closest first (agreement tempered by how much is shared).
    /// </summary>
    /// <param name="candidates">Each such person, with the average difference in stars over the shared shows.</param>
    internal static List<Twin> Twins(IEnumerable<(string UserId, int Shared, double Difference)> candidates) => candidates
        .Select(c => new Twin(c.UserId, c.Shared, 1 - c.Difference / 4.0))
        .Where(t => t.Agreement >= MinAgreement)
        .OrderByDescending(t => t.Agreement * t.Shared / (t.Shared + 3.0))
        // People who agree equally are common, and they come from the database in no order: without this, which 25
        // are the twins (and so the picks) could change between two requests.
        .ThenBy(t => t.UserId, StringComparer.Ordinal)
        .Take(MaxTwins)
        .ToList();

    /// <summary>
    /// Whether there are enough twins to say anything: under <see cref="InsightRules.MinUsersToShow"/>, nothing is
    /// shown, not even how many.
    /// </summary>
    internal static bool Enough(IReadOnlyCollection<Twin> twins) => twins.Count >= InsightRules.MinUsersToShow;

    /// <summary>
    /// What the twins loved that the viewer doesn't know (the picks, best first) and what they and the viewer both
    /// loved; each needs <see cref="InsightRules.MinUsersToShow"/> twins behind it.
    /// </summary>
    /// <param name="mine">The viewer's stars per show.</param>
    /// <param name="known">Shows the viewer has rated, followed or watched.</param>
    internal static (List<Pick> Picks, List<Guid> AgreeOn) Choose(IReadOnlyCollection<Loved> theirs, IReadOnlyList<Twin> twins,
        IReadOnlyDictionary<Guid, int> mine, IReadOnlySet<Guid> known)
    {
        var weight = twins.ToDictionary(t => t.UserId, t => t.Agreement);
        var picks = theirs.Where(r => !known.Contains(r.ShowId))
            .GroupBy(r => r.ShowId)
            .Where(g => g.Select(r => r.UserId).Distinct().Count() >= InsightRules.MinUsersToShow)
            .Select(g => (Pick: new Pick(g.Key, g.Select(r => r.UserId).Distinct().Count(), g.Average(r => r.Stars)),
                Score: g.Sum(r => weight[r.UserId] * (r.Stars - 3))))
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.Pick.Stars)
            .Take(MaxPicks)
            .Select(p => p.Pick)
            .ToList();
        var agreeOn = theirs.Where(r => mine.GetValueOrDefault(r.ShowId) >= LovedStars)
            .GroupBy(r => r.ShowId)
            .Where(g => g.Select(r => r.UserId).Distinct().Count() >= InsightRules.MinUsersToShow)
            .OrderByDescending(g => g.Count())
            .Take(MaxAgreeOn)
            .Select(g => g.Key)
            .ToList();
        return (picks, agreeOn);
    }

    /// <summary>
    /// The answer, with the chosen shows named; one that's gone from the catalog is left out. With too few twins
    /// (<see cref="Enough"/>) it is <see cref="TooFewTwins"/>, whatever was chosen.
    /// </summary>
    internal static TasteTwinsDto Of(int rated, IReadOnlyCollection<Twin> twins, IReadOnlyCollection<Pick> picks, IEnumerable<Guid> agreeOn,
        IReadOnlyDictionary<Guid, ShowCopy> shows) => !Enough(twins) ? TooFewTwins(rated) : new(
        rated,
        twins.Count,
        Math.Round(twins.Average(t => t.Agreement), 2),
        picks.Count == 0 ? "Your twins haven't loved anything you haven't seen yet." : null,
        agreeOn.Where(shows.ContainsKey).Select(id => new TwinShowDto(id, shows[id].Title, shows[id].PosterUrl)).ToList(),
        picks.Where(p => shows.ContainsKey(p.ShowId))
            .Select(p => new TwinPickDto(p.ShowId, shows[p.ShowId].Title, shows[p.ShowId].PosterUrl, p.Loved, Math.Round(p.Stars, 1),
                shows[p.ShowId].ExternalRating))
            .ToList());

    private static TasteTwinsDto Empty(int rated, int twins, double? agreement, string reason) =>
        new(rated, twins, agreement, reason, [], []);
}

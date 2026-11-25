namespace TvTracker.InsightsEngine;

/// <summary>
/// How much a show is likely worth the user's time, for backlog triage and "Fits your time off": the provider's rating,
/// nudged by whether its genres are ones the user likes and by how it ends. A score on the rating's 0-10 scale, so
/// "worth starting" reads as a rating would. Kept deliberately simple and explained by its reasons.
/// </summary>
internal static class ShowValue
{
    /// <summary>
    /// Shows without a provider rating count as a typical show.
    /// </summary>
    public const double UnratedValue = 7.0;

    /// <summary>
    /// All of a show's genres liked adds half this; none of them takes half away.
    /// </summary>
    private const double TasteWeight = 0.6;

    /// <summary>
    /// A show watched this far (of what's aired) counts as one the user liked.
    /// </summary>
    internal const int LikedStars = 4;

    internal const double FinishedShare = 0.8;

    /// <param name="Landing">The <see cref="LandingVerdicts"/> verdict, for ended shows that have one.</param>
    /// <param name="SlowStart">Where a slow start turns, when it's <see cref="InsightRules.SlowStartMinutes"/> or more in.</param>
    /// <param name="RatingReason">The "Rated 8.2" line in <paramref name="Reasons"/>, when there is one, so a caller showing the
    /// rating another way can leave it out without matching its text.</param>
    /// <param name="TasteReason">The "Your kind of show: …" line, likewise.</param>
    internal sealed record Assessment(double Value, IReadOnlyList<string> Reasons, string? Landing, GetsGoodTurn? SlowStart, bool Limited,
        string? RatingReason = null, string? TasteReason = null);

    /// <summary>
    /// The shows the viewer liked: rated <see cref="LikedStars"/> stars or more, or watched at least
    /// <see cref="FinishedShare"/> of.
    /// </summary>
    /// <param name="watched">Episodes watched per show.</param>
    /// <param name="aired">Aired regular episodes per show.</param>
    internal static List<Guid> LikedShows(IReadOnlyDictionary<Guid, int> stars, IReadOnlyDictionary<Guid, int> watched, IReadOnlyDictionary<Guid, int> aired) =>
        stars.Where(s => s.Value >= LikedStars).Select(s => s.Key)
            .Union(watched.Where(w => aired.TryGetValue(w.Key, out var episodes) && w.Value >= episodes * FinishedShare).Select(w => w.Key))
            .ToList();

    /// <summary>
    /// The genres of the liked shows. With four or more such shows, a genre needs two of them, so one stray show
    /// doesn't define a taste.
    /// </summary>
    internal static HashSet<string> LikedGenres(IReadOnlyCollection<IReadOnlyList<string>> likedShowsGenres)
    {
        var needed = likedShowsGenres.Count >= 4 ? 2 : 1;
        return likedShowsGenres.SelectMany(g => g).GroupBy(g => g).Where(g => g.Count() >= needed).Select(g => g.Key).ToHashSet();
    }

    internal static Assessment Assess(ShowCopy show, ShowInsights? insights, HashSet<string> liked) =>
        Assess(show, insights?.Landing?.Verdict, insights?.GetsGood?.Turn, insights?.Tags?.Any(t => t.Key == ShowTags.LimitedSeries) == true, liked);

    /// <param name="landing">The show's <see cref="LandingVerdicts"/> verdict, when it has one.</param>
    /// <param name="turn">Where a slow start turns, when the document found one.</param>
    /// <param name="limited">Tagged a limited series.</param>
    internal static Assessment Assess(ShowCopy show, string? landing, GetsGoodTurn? turn, bool limited, HashSet<string> liked) =>
        Assess(show.ExternalRating, show.Genres, landing, turn, limited, liked);

    /// <param name="externalRating">The provider's rating; a show without one counts as <see cref="UnratedValue"/>.</param>
    internal static Assessment Assess(double? externalRating, IReadOnlyList<string> genres, string? landing, GetsGoodTurn? turn, bool limited,
        HashSet<string> liked)
    {
        var reasons = new List<string>();
        var value = externalRating ?? UnratedValue;
        string? ratingReason = null;
        string? tasteReason = null;
        if (externalRating is { } rating)
        {
            ratingReason = $"Rated {rating:0.0}";
            reasons.Add(ratingReason);
        }

        if (liked.Count > 0 && genres.Count > 0)
        {
            var shared = genres.Where(liked.Contains).ToList();
            value += TasteWeight * ((double)shared.Count / genres.Count - 0.5);
            if (shared.Count > 0)
            {
                tasteReason = $"Your kind of show: {string.Join(", ", shared)}";
                reasons.Add(tasteReason);
            }
        }

        if (LandingVerdicts.Ending(landing) is { } ending)
        {
            value += ending.Points;
            reasons.Add(ending.Text);
        }

        var slowStart = turn is { MinutesBefore: >= InsightRules.SlowStartMinutes } ? turn : null;
        if (slowStart is not null)
        {
            reasons.Add($"Slow start: gets good at S{slowStart.SeasonNumber:00}E{slowStart.EpisodeNumber:00}, {Hours(slowStart.MinutesBefore)} in");
        }

        return new Assessment(Math.Round(value, 2), reasons, landing, slowStart, limited, ratingReason, tasteReason);
    }

    /// <summary>
    /// "45 min", "3 h": hours rounded once past two.
    /// </summary>
    internal static string Hours(int minutes) => minutes < 120 ? $"{minutes} min" : $"{Math.Round(minutes / 60.0):N0} h";
}

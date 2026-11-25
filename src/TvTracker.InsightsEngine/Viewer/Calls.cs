namespace TvTracker.InsightsEngine;

/// <summary>
/// A show's "Call it" panel: its next season to call, and the caller's calls on it.
/// </summary>
/// <param name="SeasonNumber">The season open to a call; null for an ended show (only past calls then).</param>
/// <param name="PremieresAtUtc">Its first dated episode, when one is listed.</param>
/// <param name="Mine">The caller's call on that season.</param>
/// <param name="Model">The forecast model's rating for it, with its 80% range; only once the caller has called, so it
/// doesn't anchor them.</param>
/// <param name="Crowd">Everyone's average call, with how many; only once the caller has called, and with 5+ callers.</param>
/// <param name="Past">The caller's earlier calls on this show, latest first.</param>
public record ShowCallDto(int? SeasonNumber, DateTimeOffset? PremieresAtUtc, double? Mine, CallRangeDto? Model, CrowdCallDto? Crowd,
    IReadOnlyList<CallDto> Past);

public record CallRangeDto(double Rating, double Low, double High);

public record CrowdCallDto(double Rating, int Callers);

/// <param name="State">"open" (before its premiere), "airing" (premiered, not over yet) or "scored".</param>
/// <param name="Model">The model's rating when the call was made; null when it had none.</param>
/// <param name="Actual">The season's average episode rating, once scored.</param>
/// <param name="BeatModel">Scored with a model: true when the caller was closer, false when the model was; null on a tie.</param>
public record CallDto(Guid ShowId, string Title, string? PosterUrl, int SeasonNumber, double Rating, double? Model, double? Actual,
    string State, bool? BeatModel, DateTimeOffset? PremieresAtUtc);

/// <param name="Scored">Calls scored so far; <paramref name="AverageMiss"/> is the caller's mean distance from the
/// actual rating, and <paramref name="ModelAverageMiss"/> the model's on the same calls (those it had a forecast for).</param>
public record CallRecordDto(int Scored, double? AverageMiss, double? ModelAverageMiss, int BeatModel, int ModelWon);

/// <param name="Suggestions">Followed running shows with a next season not called yet, soonest premiere first.</param>
public record MyCallsDto(CallRecordDto Record, IReadOnlyList<CallDto> Calls, IReadOnlyList<CallDto> Suggestions);

/// <summary>
/// Call it: predict a season's average episode rating before it premieres, and see whether you beat the forecast model
/// (<see cref="RatingForecast"/>) once it's over.
/// <list type="bullet">
/// <item><b>Which season:</b> a running show's next one, the season after the latest to air. A call can be made or
/// changed until its first episode airs; then it's locked.</item>
/// <item><b>No anchoring:</b> the model's forecast and the crowd's average are shown only after the caller has called.
/// The model's rating is kept with the call as it stood then, since the forecast moves as the show airs.</item>
/// <item><b>Scoring</b> is Analytics.Worker's (<c>SeasonCallScorer</c>): the season's average provider rating once it's
/// over. A miss is the distance from it; beating the model is being closer than it was.</item>
/// <item><b>The privacy rule:</b> the crowd's average needs 5 or more callers.</item>
/// </list>
/// </summary>
public static class Calls
{
    internal const int MaxSuggestions = 12;

    /// <summary>
    /// Whether a scored call beat the model's forecast: nearer the actual rating (to two decimals); null when it isn't
    /// scored, had no forecast, or they tied.
    /// </summary>
    internal static bool? Beat(double rating, double? model, double? actual)
    {
        if (actual is not { } scored || model is not { } forecast)
        {
            return null;
        }
        var mine = Math.Round(Math.Abs(rating - scored), 2);
        var theirs = Math.Round(Math.Abs(forecast - scored), 2);
        return mine == theirs ? null : mine < theirs;
    }

    /// <summary>
    /// Where a call stands: "scored" once the season's actual rating is in, "airing" from its premiere, else "open".
    /// </summary>
    internal static string State(double? actual, DateTimeOffset? premieres, DateTimeOffset now) =>
        actual is not null ? "scored" : premieres is { } p && p <= now ? "airing" : "open";

    /// <summary>
    /// The viewer's record over their scored calls, against the model's forecasts where there was one.
    /// </summary>
    internal static CallRecordDto Record(IReadOnlyCollection<(double Rating, double? ModelRating, double? Actual)> calls)
    {
        var scored = calls.Where(c => c.Actual is not null).ToList();
        var withModel = scored.Where(c => c.ModelRating is not null).ToList();
        return new CallRecordDto(
            scored.Count,
            scored.Count > 0 ? Math.Round(scored.Average(c => Math.Abs(c.Rating - c.Actual!.Value)), 2) : null,
            withModel.Count > 0 ? Math.Round(withModel.Average(c => Math.Abs(c.ModelRating!.Value - c.Actual!.Value)), 2) : null,
            withModel.Count(c => Beat(c.Rating, c.ModelRating, c.Actual) == true),
            withModel.Count(c => Beat(c.Rating, c.ModelRating, c.Actual) == false));
    }

    /// <summary>
    /// The calls in the order they're shown: airing, then open, then scored; soonest premiere first within each.
    /// </summary>
    internal static List<CallDto> Ordered(IEnumerable<CallDto> calls) => calls
        .OrderBy(c => c.State switch { "airing" => 0, "open" => 1, _ => 2 })
        .ThenBy(c => c.PremieresAtUtc ?? DateTimeOffset.MaxValue)
        .ToList();

    /// <summary>
    /// What everyone called a season, once <see cref="InsightRules.MinUsersToShow"/> have; null before.
    /// </summary>
    internal static CrowdCallDto? Crowd(int callers, double average) =>
        callers >= InsightRules.MinUsersToShow ? new CrowdCallDto(Math.Round(average, 1), callers) : null;
}

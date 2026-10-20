namespace Analytics.Data;

/// <summary>
/// Everything Analytics knows about one show, computed hourly and served as-is by Analytics.API.
/// Our users' averages appear only once at least <see cref="InsightRules.MinRatingsToShow"/> ratings back them.
/// </summary>
/// <param name="Trend">"rising", "falling" or "steady" across seasons; null with fewer than three rated seasons.</param>
public record ShowInsights(
    Guid ShowId,
    string Title,
    DateTime ComputedAtUtc,
    RatingPair Rating,
    IReadOnlyList<SeasonStats> Seasons,
    SeasonHighlight? BestSeason,
    SeasonHighlight? WorstSeason,
    string? Trend,
    IReadOnlyList<TopEpisode> TopEpisodes,
    RunFacts Run,
    Premiere? NextPremiere,
    IReadOnlyList<SharedCast> SharedCast);

/// <param name="External">The catalog provider's rating (0-10), e.g. TVmaze's.</param>
/// <param name="Ours">Our users' average stars (1-5); null below the minimum number of ratings.</param>
/// <param name="OurCount">How many of our users rated it.</param>
public record RatingPair(double? External, double? Ours, int OurCount);

public record SeasonStats(
    int Number,
    int EpisodeCount,
    int AiredCount,
    DateTimeOffset? FirstAirsAtUtc,
    DateTimeOffset? LastAirsAtUtc,
    int RuntimeMinutes,
    RatingPair Rating);

/// <param name="Source">"external" when ranked by the provider's ratings, "ours" when by our users'.</param>
public record SeasonHighlight(int Number, double Rating, string Source);

public record TopEpisode(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Title, DateTimeOffset? AirsAtUtc, RatingPair Rating);

/// <param name="Seasons">Seasons with at least one aired episode.</param>
public record RunFacts(
    int Seasons,
    int AiredEpisodes,
    int RuntimeMinutes,
    DateTimeOffset? FirstAiredUtc,
    DateTimeOffset? LastAiredUtc,
    SeasonBreak? LongestBreak);

/// <summary>The gap between the last episode of one season and the first of the next.</summary>
public record SeasonBreak(int AfterSeason, int BeforeSeason, int Days);

/// <param name="DaysSincePreviousSeason">Days from the previous season's last episode to the premiere.</param>
/// <param name="CatchUpEpisodes">Episodes aired before the premiere: what to watch to be ready.</param>
public record Premiere(
    int SeasonNumber,
    DateTimeOffset PremieresAtUtc,
    int? DaysSincePreviousSeason,
    int CatchUpEpisodes,
    int CatchUpRuntimeMinutes);

/// <summary>An actor (or character) this show shares with another show in the catalog.</summary>
/// <param name="SameCharacter">True when the provider lists the same character in both, not just the same actor.</param>
public record SharedCast(
    string PersonName,
    string? CharacterHere,
    Guid OtherShowId,
    string OtherShowTitle,
    string? OtherShowPosterUrl,
    string? CharacterThere,
    bool SameCharacter);

public static class InsightRules
{
    /// <summary>Our users' averages are hidden until this many ratings back them, so no one's rating can be singled out.</summary>
    public const int MinRatingsToShow = 5;
}

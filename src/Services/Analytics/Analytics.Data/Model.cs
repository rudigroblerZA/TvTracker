using TvTracker.IntegrationEvents;

namespace Analytics.Data;

// Analytics' copy of our users' ratings, follows and watches, built from events. Its copy of the catalog is among the
// insights engine's facts.

public class UserShowRating
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public int Stars { get; set; }
}

public class UserEpisodeRating
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int Stars { get; set; }
}

/// <summary>
/// A user following a show (from ShowFollowed; removed by ShowUnfollowed).
/// </summary>
public class UserFollow
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime FollowedAtUtc { get; set; }
}

/// <summary>
/// Copy of a user's effective plan (<see cref="TvTracker.IntegrationEvents.Plans"/>), from Identity's plan events and
/// daily profile snapshot, kept by Analytics.Worker; no row means free.
/// </summary>
public class UserPlanCopy
{
    public required string UserId { get; set; }
    public required string Plan { get; set; }

    /// <summary>
    /// When Identity set it; an older event is ignored, since events can arrive out of order.
    /// </summary>
    public DateTimeOffset ChangedAtUtc { get; set; }
}

/// <summary>
/// A streaming service the user says they pay for (<see cref="TvTracker.IntegrationEvents.NetworkServices"/> key).
/// The user's own setting, written by Analytics.API, where the report on it is computed.
/// </summary>
public class UserService
{
    public required string UserId { get; set; }
    public required string ServiceKey { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>
/// "Call it": a user's prediction of a season's average episode rating, made before it premieres. Written by
/// Analytics.API (the user's call, with the forecast model's as it stood then), scored by Analytics.Worker once the
/// season is over.
/// </summary>
public class SeasonCall
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }

    /// <summary>
    /// The user's call, 1-10 in tenths.
    /// </summary>
    public double Rating { get; set; }

    /// <summary>
    /// The forecast model's rating for the season when the call was made or last changed; null without one.
    /// </summary>
    public double? ModelRating { get; set; }

    public DateTime CalledAtUtc { get; set; }

    /// <summary>
    /// The season's average episode rating on the provider, once it's over.
    /// </summary>
    public double? Actual { get; set; }

    public DateTime? ScoredAtUtc { get; set; }
}

// Computed every hour by Analytics.Worker; Analytics.API only reads these.

/// <summary>
/// One show's insights as a JSON <see cref="ShowInsights"/> document.
/// </summary>
public class ShowInsightsRow
{
    public Guid ShowId { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>
/// A season premiering soon, for the premieres list.
/// </summary>
public class PremiereRow
{
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public required string ShowTitle { get; set; }
    public string? PosterUrl { get; set; }
    public List<string> Genres { get; set; } = [];
    public DateTimeOffset PremieresAtUtc { get; set; }
    public double? ShowExternalRating { get; set; }
    public int? DaysSincePreviousSeason { get; set; }
    public int CatchUpEpisodes { get; set; }
    public int CatchUpRuntimeMinutes { get; set; }

    /// <summary>
    /// Follows in the last four weeks, for "most anticipated"; null below the minimum number of followers.
    /// </summary>
    public int? RecentFollows { get; set; }
}

/// <summary>
/// A top-rated episode, ranked overall (<see cref="Genre"/> empty) and within each genre.
/// </summary>
public class TopEpisodeRow
{
    public required string Genre { get; set; }
    public int Rank { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public required string ShowTitle { get; set; }
    public string? PosterUrl { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset? AirsAtUtc { get; set; }
    public double ExternalRating { get; set; }
    public double? OurRating { get; set; }
    public int OurRatingCount { get; set; }
}

/// <summary>
/// One entry of a discovery list (see <see cref="DiscoveryLists"/>), overall (<see cref="Genre"/> "") or within a genre.
/// Episode fields are set for episode lists (best finales).
/// </summary>
public class DiscoveryRow
{
    public required string List { get; set; }
    public required string Genre { get; set; }
    public int Rank { get; set; }
    public Guid ShowId { get; set; }
    public required string ShowTitle { get; set; }
    public string? PosterUrl { get; set; }
    public double? ShowExternalRating { get; set; }

    /// <summary>
    /// What the list ranks by: the show's rating, its episode-rating spread, or the finale's rating.
    /// </summary>
    public double Value { get; set; }

    public Guid? EpisodeId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeTitle { get; set; }
}

/// <summary>
/// A creator's fingerprint (<see cref="CreatorFingerprint"/> as JSON), recomputed hourly with the shows'.
/// </summary>
public class CreatorFingerprintRow
{
    public Guid PersonId { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>
/// A network's report card (<see cref="NetworkReport"/> as JSON), recomputed hourly with the shows'.
/// </summary>
public class NetworkReportRow
{
    public required string Network { get; set; }
    public int Shows { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>
/// An actor's insights (<see cref="PersonInsights"/> as JSON), recomputed hourly with the shows'.
/// </summary>
public class PersonInsightsRow
{
    public Guid PersonId { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

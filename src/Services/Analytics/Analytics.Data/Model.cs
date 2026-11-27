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
/// A Taste Match: an invitation from a member to compare TV taste with one friend, and, once the friend has answered,
/// the result. Read by anyone who has the <see cref="Token"/>, which is the only secret, so the result holds only what
/// may be shown (see the engine's <c>TasteMatch</c>). Neither side's shows are stored for a member: they are read when
/// the friend answers, and only the result is kept. A guest friend's picks are kept with the match and go with it.
/// </summary>
public class TasteMatchRow
{
    /// <summary>
    /// 32 random hex characters: the link.
    /// </summary>
    public required string Token { get; set; }

    public required string InviterUserId { get; set; }

    /// <summary>
    /// The inviter's first name as it was when they started the match; shown to whoever opens the link.
    /// </summary>
    public string? InviterDisplayName { get; set; }

    /// <summary>
    /// The friend, when they answered signed in; null for a guest and while the match is open.
    /// </summary>
    public string? FriendUserId { get; set; }

    /// <summary>
    /// The friend's first name: a member's, or what a guest typed (optional).
    /// </summary>
    public string? FriendDisplayName { get; set; }

    /// <summary>
    /// A guest friend's picks; empty for a member.
    /// </summary>
    public List<Guid> GuestPicks { get; set; } = [];

    /// <summary>
    /// Given to a guest when they answer, so the device that made the picks can delete the match. Null for a member.
    /// </summary>
    public string? GuestKey { get; set; }

    /// <summary>
    /// Shows the inviter asked not to have named in the result; scored all the same.
    /// </summary>
    public List<Guid> InviterUnnamed { get; set; } = [];

    public List<Guid> FriendUnnamed { get; set; } = [];

    /// <summary>
    /// The result (the engine's <c>TasteMatchDto</c>, as JSON), once the friend has answered: a record of that day,
    /// never recomputed. Null while the match is open.
    /// </summary>
    public string? Result { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? DoneAtUtc { get; set; }

    /// <summary>
    /// When the match is deleted: an open link after <see cref="OpenFor"/>, a done match after <see cref="KeptFor"/>.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Changes with every save of the row (Postgres's own xmin), so of two answers arriving together only one is saved.
    /// </summary>
    public uint Version { get; set; }

    public static readonly TimeSpan OpenFor = TimeSpan.FromDays(30);
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(365);
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

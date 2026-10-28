using TvTracker.IntegrationEvents;

namespace Analytics.Data;

// Analytics' copy of the catalog and of our users' ratings, built from events.

public class ShowCopy
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public double Popularity { get; set; }

    /// <summary>The catalog provider's average rating (0-10), e.g. TVmaze's.</summary>
    public double? ExternalRating { get; set; }

    /// <summary>Whether a catalog event has carried the show's creatives yet (they may be none).</summary>
    public bool CreativesKnown { get; set; }

    /// <summary>Its network or streaming service ("HBO", "Netflix"), for the network report cards.</summary>
    public string? Network { get; set; }

    /// <summary>Whether a catalog event has carried the network yet (it may be none).</summary>
    public bool NetworkKnown { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Someone who made a show (creator, developer or executive producer), from catalog events.</summary>
public class CreativeCopy
{
    public Guid ShowId { get; set; }
    public Guid PersonId { get; set; }
    public required string Name { get; set; }
    public List<string> Roles { get; set; } = [];
    public int Order { get; set; }
}

/// <summary>A person's career behind the camera across the catalog provider, from catalog events.</summary>
public class PersonCareerCopy
{
    public Guid PersonId { get; set; }
    public required string Name { get; set; }
    public int Shows { get; set; }
    public int Created { get; set; }
    public int ExecutiveProduced { get; set; }
}

/// <summary>
/// A show an actor is in the main cast of, anywhere on the catalog provider, from catalog events: what the reunion
/// radar looks for actors working together again in.
/// </summary>
public class PersonCastCreditCopy
{
    public Guid PersonId { get; set; }
    public required string ShowExternalId { get; set; }

    /// <summary>The catalog's id, when the show is in the catalog.</summary>
    public Guid? ShowId { get; set; }

    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public DateOnly? Premiered { get; set; }
    public double Popularity { get; set; }
    public double? Rating { get; set; }
    public string? Character { get; set; }
    public bool Voice { get; set; }
}

public class EpisodeCopy
{
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset? AirsAtUtc { get; set; }
    public int? RuntimeMinutes { get; set; }
    public double? ExternalRating { get; set; }
}

/// <summary>A season announced with a premiere date before its episodes are scheduled (from SeasonAnnounced).</summary>
public class AnnouncedSeason
{
    public Guid ShowId { get; set; }
    public int SeasonNumber { get; set; }
    public DateTimeOffset? PremieresAtUtc { get; set; }
}

/// <summary>
/// A moderator's note on an episode (from Research's <c>EpisodeNotesChanged</c>): essential or skippable, why, and the
/// source. The essential cut follows it before ratings.
/// </summary>
public class EpisodeNoteCopy
{
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }

    /// <summary>One of <c>EpisodeMarks</c>.</summary>
    public required string Mark { get; set; }

    public required string Reason { get; set; }
    public required string SourceUrl { get; set; }

    /// <summary>When Research read the notes this came with; an older event leaves the show's notes alone.</summary>
    public DateTimeOffset TakenAtUtc { get; set; }
}

public class CastCopy
{
    public Guid ShowId { get; set; }
    public required string PersonName { get; set; }

    /// <summary>The catalog's id for the person (their actor page); null for credits stored before it was sent.</summary>
    public Guid? PersonId { get; set; }

    public string? Character { get; set; }
    public List<string> CharacterExternalIds { get; set; } = [];
    public int Order { get; set; }
}

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

/// <summary>A user's watched mark (from EpisodeWatched; removed by EpisodeUnwatched).</summary>
public class UserEpisodeWatch
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }

    /// <summary>When the mark was made (the event's time), for telling stopped viewers from ones mid-binge.</summary>
    public DateTime WatchedAtUtc { get; set; }
}

/// <summary>A user following a show (from ShowFollowed; removed by ShowUnfollowed).</summary>
public class UserFollow
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime FollowedAtUtc { get; set; }
}

// Computed every hour by Analytics.Worker; Analytics.API only reads these.

/// <summary>One show's insights as a JSON <see cref="ShowInsights"/> document.</summary>
public class ShowInsightsRow
{
    public Guid ShowId { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>A season premiering soon, for the premieres list.</summary>
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

    /// <summary>Follows in the last four weeks, for "most anticipated"; null below the minimum number of followers.</summary>
    public int? RecentFollows { get; set; }
}

/// <summary>A top-rated episode, ranked overall (<see cref="Genre"/> empty) and within each genre.</summary>
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

    /// <summary>What the list ranks by: the show's rating, its episode-rating spread, or the finale's rating.</summary>
    public double Value { get; set; }

    public Guid? EpisodeId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeTitle { get; set; }
}

/// <summary>A creator's fingerprint (<see cref="CreatorFingerprint"/> as JSON), recomputed hourly with the shows'.</summary>
public class CreatorFingerprintRow
{
    public Guid PersonId { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>A network's report card (<see cref="NetworkReport"/> as JSON), recomputed hourly with the shows'.</summary>
public class NetworkReportRow
{
    public required string Network { get; set; }
    public int Shows { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>An actor's insights (<see cref="PersonInsights"/> as JSON), recomputed hourly with the shows'.</summary>
public class PersonInsightsRow
{
    public Guid PersonId { get; set; }
    public required string Document { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

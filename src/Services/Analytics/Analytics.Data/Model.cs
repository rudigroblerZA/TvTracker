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

    public DateTime UpdatedAtUtc { get; set; }
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

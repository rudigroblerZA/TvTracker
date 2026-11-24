using TvTracker.IntegrationEvents;

namespace TvTracker.ShowInsightsEngine;

// What is known about a show, as the engine is given it. Analytics keeps these as its copies of the catalog and of
// what viewers did (Analytics.Data maps them to tables); the rules read them and never load or save them.

public class ShowCopy
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
    public ShowStatus Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public double Popularity { get; set; }

    /// <summary>
    /// The catalog provider's average rating (0-10), e.g. TVmaze's.
    /// </summary>
    public double? ExternalRating { get; set; }

    /// <summary>
    /// Whether a catalog event has carried the show's creatives yet (they may be none).
    /// </summary>
    public bool CreativesKnown { get; set; }

    /// <summary>
    /// Its network or streaming service ("HBO", "Netflix"), for the network report cards.
    /// </summary>
    public string? Network { get; set; }

    /// <summary>
    /// Whether a catalog event has carried the network yet (it may be none).
    /// </summary>
    public bool NetworkKnown { get; set; }

    /// <summary>
    /// When the copy last heard about the show; not read by any insight, kept for looking into a stale copy.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Someone who made a show (creator, developer or executive producer), from catalog events.
/// </summary>
public class CreativeCopy
{
    public Guid ShowId { get; set; }
    public Guid PersonId { get; set; }
    public required string Name { get; set; }
    public List<string> Roles { get; set; } = [];
    public int Order { get; set; }
}

/// <summary>
/// A person's career behind the camera across the catalog provider, from catalog events.
/// </summary>
public class PersonCareerCopy
{
    public Guid PersonId { get; set; }
    public required string Name { get; set; }
    public int Shows { get; set; }
    public int Created { get; set; }
    public int ExecutiveProduced { get; set; }
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

/// <summary>
/// A season announced with a premiere date before its episodes are scheduled (from SeasonAnnounced).
/// </summary>
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

    /// <summary>
    /// The catalog's id for the person (their actor page); null for credits stored before it was sent.
    /// </summary>
    public Guid? PersonId { get; set; }

    public string? Character { get; set; }
    public List<string> CharacterExternalIds { get; set; } = [];
    public int Order { get; set; }
}

/// <summary>
/// A user's watched mark (from EpisodeWatched; removed by EpisodeUnwatched).
/// </summary>
public class UserEpisodeWatch
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }

    /// <summary>
    /// When the user watched (the mark's time, which the phone backdates), for pace and bulk marks.
    /// </summary>
    public DateTime WatchedAtUtc { get; set; }

    /// <summary>
    /// When the mark reached us (the event's time), which a snapshot repair compares with when it was taken.
    /// </summary>
    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>
/// A show an actor is in the main cast of, anywhere on the catalog provider, from catalog events: what the reunion
/// radar looks for actors working together again in.
/// </summary>
public class PersonCastCreditCopy
{
    public Guid PersonId { get; set; }
    public required string ShowExternalId { get; set; }

    /// <summary>
    /// The catalog's id, when the show is in the catalog.
    /// </summary>
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

/// <summary>
/// A moderator's note on an episode (from Research's <c>EpisodeNotesChanged</c>): essential or skippable, why, and the
/// source. The essential cut follows it before ratings.
/// </summary>
public class EpisodeNoteCopy
{
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }

    /// <summary>
    /// One of <c>EpisodeMarks</c>.
    /// </summary>
    public required string Mark { get; set; }

    public required string Reason { get; set; }
    public required string SourceUrl { get; set; }

    /// <summary>
    /// When Research read the notes this came with; an older event leaves the show's notes alone.
    /// </summary>
    public DateTimeOffset TakenAtUtc { get; set; }
}

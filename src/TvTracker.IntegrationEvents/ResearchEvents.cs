using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Research.API

/// <summary>
/// Episode notes as Research has them: moderators' marks of episodes as essential or skippable, with a reason and a
/// source. Sent with a show's whole list whenever one changes, and for every show after start and daily, so a lost
/// event heals.
/// </summary>
/// <param name="ShowId">The show whose notes these are; null for every show (the daily send), when a show missing
/// from <paramref name="Notes"/> has none.</param>
/// <param name="TakenAtUtc">When the notes were read. A consumer ignores one older than what it has.</param>
public record EpisodeNotesChangedIntegrationEvent(Guid? ShowId, IReadOnlyList<EpisodeNoteItem> Notes, DateTimeOffset TakenAtUtc)
    : IntegrationEvent;

/// <param name="Mark">One of <see cref="EpisodeMarks"/>.</param>
/// <param name="Reason">Why, in a line, shown to viewers ("Filler: not from the manga").</param>
/// <param name="SourceUrl">Where the moderator found it.</param>
public record EpisodeNoteItem(Guid ShowId, Guid EpisodeId, string Mark, string Reason, string SourceUrl);

public static class EpisodeMarks
{
    /// <summary>Kept by the essential cut whatever its rating.</summary>
    public const string Essential = "essential";

    /// <summary>Skipped by the essential cut; a season with one is cut by its notes alone.</summary>
    public const string Skippable = "skippable";

    public static bool IsValid(string mark) => mark is Essential or Skippable;
}

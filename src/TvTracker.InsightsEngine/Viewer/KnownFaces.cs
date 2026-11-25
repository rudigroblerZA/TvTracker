namespace TvTracker.InsightsEngine;

/// <param name="CastCount">The show's cast members with a person, known or not.</param>
public record KnownFacesDto(Guid ShowId, int CastCount, IReadOnlyList<KnownFaceDto> People);

/// <param name="Character">Who they play in this show.</param>
/// <param name="From">The user's other shows they're in, caught-up ones first, then by how much the user watched.</param>
public record KnownFaceDto(Guid PersonId, string Name, string? Character, IReadOnlyList<KnownFromDto> From);

/// <param name="Character">Who they play there.</param>
/// <param name="Watched">Regular episodes of that show the user watched.</param>
/// <param name="Aired">Regular episodes of that show aired so far.</param>
/// <param name="CaughtUp">
/// The user has seen that show's latest aired episode, or rated the show without marking episodes. Otherwise the actor
/// may join it after where the user is, so the UI treats the line as a spoiler.
/// </param>
public record KnownFromDto(Guid ShowId, string Title, string? PosterUrl, string? Character, int Watched, int Aired, bool CaughtUp);

/// <summary>
/// "Where do I know them from?": for each actor in a show's cast, the other shows the user has seen them in. Computed
/// on request from the user's own watches and ratings and the catalog's main casts, so it needs no minimum counts.
/// <list type="bullet">
/// <item><b>Seen:</b> a show with a watched episode, or rated. Followed alone doesn't count: it says nothing about
/// having watched it.</item>
/// <item><b>Main casts only:</b> TVmaze lists no guest roles, so a guest spot in a show the user watched isn't found.</item>
/// <item><b>Spoilers:</b> a cast is today's main cast, with no season per role, so an actor in a show the user is
/// partway through may only arrive later. Those lines say so (<see cref="KnownFromDto.CaughtUp"/>) rather than being
/// left out, since most are from the start.</item>
/// </list>
/// </summary>
public static class KnownFaces
{
    private const int MaxFromPerPerson = 3;

    /// <summary>
    /// The cast members the viewer knows from elsewhere, in billing order, each with the shows they know them from.
    /// </summary>
    /// <param name="cast">The show's cast, in billing order.</param>
    /// <param name="elsewhere">Their roles in the other shows the viewer has rated or watched, by person.</param>
    /// <param name="rated">The shows the viewer has rated.</param>
    internal static List<KnownFaceDto> Of(IEnumerable<CastCopy> cast, ILookup<Guid, CastCopy> elsewhere, IReadOnlyDictionary<Guid, ShowCopy> shows,
        IReadOnlyDictionary<Guid, ShowProgress> progress, IReadOnlySet<Guid> rated) =>
        cast.DistinctBy(c => c.PersonId)
            .Select(member => new KnownFaceDto(member.PersonId!.Value, member.PersonName, member.Character,
                elsewhere[member.PersonId!.Value]
                    .Where(c => shows.ContainsKey(c.ShowId))
                    .DistinctBy(c => c.ShowId)
                    .Select(c =>
                    {
                        var p = progress.GetValueOrDefault(c.ShowId);
                        var show = shows[c.ShowId];
                        // Caught up is the latest episode rather than every one, since people skip the odd episode.
                        return new KnownFromDto(c.ShowId, show.Title, show.PosterUrl, c.Character, p?.Watched ?? 0, p?.Aired ?? 0,
                            p?.LatestWatched == true || p?.Watched is null or 0 && rated.Contains(c.ShowId));
                    })
                    .OrderByDescending(f => f.CaughtUp)
                    .ThenByDescending(f => f.Watched)
                    .Take(MaxFromPerPerson)
                    .ToList()))
            .Where(p => p.From.Count > 0)
            .ToList();
}

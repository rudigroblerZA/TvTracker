namespace TvTracker.ShowInsightsEngine;

/// <param name="Dna">The show's DNA traits by key, when its document has them (While you wait compares them).</param>
[System.ComponentModel.ImmutableObject(true)]
public sealed record ShelfShow(Guid ShowId, string Title, string? PosterUrl, double? ExternalRating, IReadOnlyList<string> Genres,
    int Episodes, int Minutes, string? Landing, GetsGoodTurn? Turn, bool Limited, IReadOnlyDictionary<string, double>? Dna = null);

/// <summary>
/// A finished show the caller hasn't started, with how much it's worth to them.
/// </summary>
internal sealed record ShelfCandidate(ShelfShow Show, ShowValue.Assessment Assessment);

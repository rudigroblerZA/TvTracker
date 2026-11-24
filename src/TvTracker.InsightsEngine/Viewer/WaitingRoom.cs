namespace TvTracker.InsightsEngine;

/// <param name="Shows">Soonest back first: dated returns, then forecasts by their likely date, then the unknown.</param>
public record WaitingRoomDto(IReadOnlyList<WaitingShowDto> Shows);

/// <param name="Kind"><c>episode</c> (mid-season, next episode dated), <c>season</c> (next season dated),
/// <c>forecast</c> (from the show's own waits), or <c>unknown</c>.</param>
/// <param name="BackAtUtc">The dated episode or premiere; null for forecasts and the unknown.</param>
/// <param name="SeasonNumber">The season it's back with, when known.</param>
/// <param name="Likely">For a forecast, with <paramref name="Earliest"/> and <paramref name="Latest"/> (the 80% range).</param>
/// <param name="Overdue">A forecast already past its range.</param>
/// <param name="Renewal">For an undated return, how often comparable shows came back (a weak signal; 0-1).</param>
/// <param name="LastAiredUtc">Its latest aired episode, which they've watched.</param>
public record WaitingShowDto(Guid ShowId, string Title, string? PosterUrl, string? Network, string Kind, DateTimeOffset? BackAtUtc,
    int? SeasonNumber, DateOnly? Likely, DateOnly? Earliest, DateOnly? Latest, bool Overdue, double? Renewal, DateTimeOffset? LastAiredUtc);

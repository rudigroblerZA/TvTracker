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

/// <summary>
/// The waiting room: running shows the caller is caught up on (every aired regular episode watched), with when each is
/// back. Computed on request from the caller's own watches and the catalog copy, plus the hourly show documents'
/// <see cref="ShowInsights.NextPremiere"/> and <see cref="Outlook"/>; ended shows have nothing to wait for.
/// </summary>
public static class WaitingRoom
{
    /// <summary>
    /// When a running show the viewer is caught up on is back: the next episode of the season under way, else a dated
    /// premiere (the document's, or a listed one), else the forecast from the show's own waits, else unknown.
    /// </summary>
    /// <param name="currentSeason">The latest season with an aired episode.</param>
    /// <param name="lastAired">Its latest aired episode's air time.</param>
    /// <param name="next">The soonest listed episode still to air.</param>
    internal static WaitingShowDto Of(ShowCopy show, ShowInsights? insights, int currentSeason, DateTimeOffset? lastAired,
        (int SeasonNumber, DateTimeOffset? AirsAtUtc)? next)
    {
        var id = show.ShowId;
        if (next is { } episode && episode.SeasonNumber == currentSeason)
        {
            return new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "episode", episode.AirsAtUtc, episode.SeasonNumber,
                null, null, null, false, null, lastAired);
        }
        if (insights?.NextPremiere is { } premiere)
        {
            return new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "season", premiere.PremieresAtUtc,
                premiere.SeasonNumber, null, null, null, false, null, lastAired);
        }
        if (next is { } listed)
        {
            return new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "season", listed.AirsAtUtc, listed.SeasonNumber,
                null, null, null, false, null, lastAired);
        }
        var renewal = insights?.Outlook?.Renewal?.Share;
        return insights?.Outlook?.NextSeason is { } forecast
            ? new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "forecast", null, forecast.SeasonNumber,
                forecast.Likely, forecast.Earliest, forecast.Latest, forecast.Overdue, renewal, lastAired)
            : new WaitingShowDto(id, show.Title, show.PosterUrl, show.Network, "unknown", null, null, null, null, null, false,
                renewal, lastAired);
    }

    /// <summary>
    /// Soonest back first: dated returns, then forecasts by their likely date, then the unknown.
    /// </summary>
    internal static List<WaitingShowDto> Ordered(IEnumerable<WaitingShowDto> shows) => shows
        .OrderBy(s => s.Kind is "episode" or "season" ? 0 : s.Kind == "forecast" ? 1 : 2)
        .ThenBy(s => s.BackAtUtc ?? (s.Likely is { } likely ? new DateTimeOffset(likely.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.MaxValue))
        .ThenByDescending(s => s.LastAiredUtc)
        .ToList();
}

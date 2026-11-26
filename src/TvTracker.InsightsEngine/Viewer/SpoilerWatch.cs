namespace TvTracker.InsightsEngine;

/// <param name="Shows">Shows with any risk, highest first.</param>
public record SpoilerWatchDto(IReadOnlyList<SpoilerRiskDto> Shows);

/// <param name="Level">"high", "medium" or "low" (see <see cref="SpoilerWatch"/>).</param>
/// <param name="Unseen">Regular episodes aired in the last <see cref="SpoilerWatch.WindowDays"/> days that the viewer hasn't watched.</param>
/// <param name="Latest">The riskiest of them; numbers only, never a title.</param>
/// <param name="Behind">Every aired regular episode the viewer hasn't watched, and their running time: what it takes to be safe.</param>
/// <param name="CalmsAround">When the risk is expected to fall to low, if nothing more airs.</param>
/// <param name="Started">The viewer has watched some of the show; a followed show not started yet is listed too.</param>
public record SpoilerRiskDto(Guid ShowId, string Title, string? PosterUrl, string Level, double Score, int Unseen, SpoilerEpisodeDto Latest,
    int Behind, int BehindMinutes, DateTimeOffset? CalmsAround, bool Started);

/// <param name="Kind">"finale", "premiere" or "episode".</param>
public record SpoilerEpisodeDto(int SeasonNumber, int EpisodeNumber, DateTimeOffset AiredAtUtc, string Kind);

/// <summary>
/// Spoiler watch: how exposed the viewer is to spoilers for each show they follow or are watching, right now. Spoilers
/// circulate in the days after an episode airs and fade as the internet moves on, so each unwatched episode aired in the
/// last <see cref="WindowDays"/> days adds its weight (a finale <see cref="FinaleWeight"/>, a premiere
/// <see cref="PremiereWeight"/>, else 1) times a decay of e^(−days / <see cref="DecayDays"/>), and the sum is scaled by
/// the show's popularity (TVmaze's, 0-100): a popular show's twist is everywhere, an obscure one's isn't.
/// <list type="bullet">
/// <item><b>Levels:</b> high from <see cref="High"/>, medium from <see cref="Medium"/>, low above <see cref="Floor"/>; below
/// that a show isn't listed. An unwatched finale of a top show on the night it airs scores 2.0.</item>
/// <item><b>Calms around:</b> the risk decays as one, so the day it falls below medium is ln(score / medium) × the decay
/// constant from now, if nothing more airs.</item>
/// <item><b>No titles:</b> an episode title is a spoiler in itself, so only season and episode numbers are sent.</item>
/// <item><b>Only the viewer's data and catalog facts,</b> so the privacy rule doesn't apply.</item>
/// </list>
/// </summary>
public static class SpoilerWatch
{
    public const int WindowDays = 14;

    public const double DecayDays = 5;

    public const double FinaleWeight = 2;

    public const double PremiereWeight = 1.5;

    public const double High = 1.0;

    public const double Medium = 0.4;

    public const double Floor = 0.1;

    /// <summary>
    /// A regular episode that aired within <see cref="WindowDays"/>.
    /// </summary>
    internal sealed record Recent(Guid ShowId, Guid EpisodeId, int SeasonNumber, int EpisodeNumber, DateTimeOffset AiredAtUtc);

    /// <summary>
    /// The risk of being spoiled, per show, riskiest first, from the recent episodes the viewer hasn't seen.
    /// </summary>
    /// <param name="finales">The last episode's number for each season with nothing more to air.</param>
    internal static List<SpoilerRiskDto> Of(IEnumerable<Recent> unseen, IReadOnlyDictionary<Guid, ShowCopy> shows,
        IReadOnlyDictionary<(Guid ShowId, int SeasonNumber), int> finales, IReadOnlyDictionary<Guid, ShowProgress> progress, DateTimeOffset now) =>
        unseen.Where(e => shows.ContainsKey(e.ShowId))
            .GroupBy(e => e.ShowId)
            .Select(g =>
            {
                var show = shows[g.Key];
                var weighted = g.Select(e =>
                    {
                        var kind = finales.TryGetValue((e.ShowId, e.SeasonNumber), out var last) && last == e.EpisodeNumber ? "finale"
                            : e.EpisodeNumber == 1 ? "premiere" : "episode";
                        var weight = kind switch { "finale" => FinaleWeight, "premiere" => PremiereWeight, _ => 1 };
                        var days = (now - e.AiredAtUtc).TotalDays;
                        return (Episode: new SpoilerEpisodeDto(e.SeasonNumber, e.EpisodeNumber, e.AiredAtUtc, kind), Risk: weight * Math.Exp(-days / DecayDays));
                    })
                    .ToList();
                var score = Math.Round(Math.Clamp(show.Popularity, 0, 100) / 100 * weighted.Sum(w => w.Risk), 2);
                var level = score >= High ? "high" : score >= Medium ? "medium" : "low";
                DateTimeOffset? calms = score >= Medium ? now.AddDays(Math.Log(score / Medium) * DecayDays) : null;
                var p = progress.GetValueOrDefault(g.Key);
                return new SpoilerRiskDto(g.Key, show.Title, show.PosterUrl, level, score, weighted.Count,
                    weighted.MaxBy(w => w.Risk).Episode, p?.Left ?? weighted.Count, p?.MinutesLeft ?? 0, calms, p?.Started == true);
            })
            .Where(r => r.Score > Floor)
            .OrderByDescending(r => r.Score)
            .ToList();
}

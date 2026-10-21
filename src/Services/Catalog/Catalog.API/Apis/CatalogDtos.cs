using Catalog.Data;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record ShowSummaryDto(
    Guid Id,
    string Title,
    string? PosterUrl,
    int? Year,
    ShowStatus Status,
    string? Network,
    IReadOnlyList<string> Genres);

public record SeasonSummaryDto(int Number, string Name, int EpisodeCount, DateTimeOffset? PremieresAtUtc);

public record CastMemberDto(string Name, string? Character, string? PhotoUrl, Guid PersonId);

/// <summary>A show someone appears in, with the character they play.</summary>
public record PersonCreditDto(ShowSummaryDto Show, string? Character);

/// <param name="Credits">Their shows in the catalog, newest first.</param>
/// <param name="Career">Their career behind the camera, from the provider; null when not known.</param>
/// <param name="Made">Catalog shows they created or executive produced, most popular first.</param>
public record PersonDetailDto(
    Guid Id,
    string Name,
    string? PhotoUrl,
    DateOnly? Birthday,
    DateOnly? Deathday,
    string? Gender,
    string? Country,
    IReadOnlyList<PersonCreditDto> Credits,
    CareerDto? Career = null,
    IReadOnlyList<MadeCreditDto>? Made = null);

/// <summary>A show someone made, with their roles on it ("Creator", "Developer", "Executive Producer").</summary>
public record MadeCreditDto(ShowSummaryDto Show, IReadOnlyList<string> Roles);

/// <summary>Someone who made a show.</summary>
/// <param name="Roles">"Creator", "Developer" and/or "Executive Producer".</param>
/// <param name="OtherShows">Other catalog shows they made, most popular first.</param>
public record CreativeDto(Guid PersonId, string Name, string? PhotoUrl, IReadOnlyList<string> Roles, CareerDto? Career,
    IReadOnlyList<ShowRefDto> OtherShows);

/// <param name="Shows">Shows they're credited on behind the camera, across the provider (TVmaze), not just this catalog.</param>
/// <param name="Prolific">At least <see cref="CareerDto.ProlificMinShows"/> shows.</param>
/// <param name="NotableShows">Their best-known shows, most popular first (titles; many aren't in the catalog).</param>
public record CareerDto(int Shows, int Created, int ExecutiveProduced, bool Prolific, IReadOnlyList<string> NotableShows)
{
    /// <summary>Behind-the-camera credits on this many shows make a "prolific" creator or producer (Dick Wolf has 57).</summary>
    public const int ProlificMinShows = 10;

    public static CareerDto? For(Person person, string? excludeTitle = null) => person.CareerShows is { } shows
        ? new CareerDto(shows, person.CareerCreated ?? 0, person.CareerExecutiveProduced ?? 0, shows >= ProlificMinShows,
            person.CareerNotableShows.Where(t => t != excludeTitle).ToList())
        : null;
}

public record ShowRefDto(Guid Id, string Title);

public record WatchProviderDto(string ProviderName, WatchProviderType Type, string? Url);

public record ShowDetailDto(
    Guid Id,
    string Title,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    DateOnly? FirstAirDate,
    ShowStatus Status,
    string? Network,
    IReadOnlyList<string> Genres,
    IReadOnlyList<SeasonSummaryDto> Seasons,
    IReadOnlyList<CastMemberDto> Cast,
    string Region,
    IReadOnlyList<WatchProviderDto> WatchProviders,
    EpisodeDto? NextEpisode,
    EpisodeDto? LastEpisode,
    IReadOnlyList<CreativeDto>? Creatives = null);

public record EpisodeDto(
    Guid Id,
    Guid ShowId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    string? Overview,
    DateTimeOffset? AirsAtUtc,
    int? RuntimeMinutes,
    EpisodeLabel Label);

/// <summary>Premiere = first episode of a season; finale = last scheduled episode of a season.</summary>
public enum EpisodeLabel
{
    None,
    SeriesPremiere,
    SeasonPremiere,
    SeasonFinale
}

public record SeasonDetailDto(Guid ShowId, int Number, string Name, string? Overview, DateTimeOffset? PremieresAtUtc, IReadOnlyList<EpisodeDto> Episodes);

public record ScheduleItemDto(Guid ShowId, string ShowTitle, string? PosterUrl, EpisodeDto Episode);

public static class CatalogMappings
{
    public static ShowSummaryDto ToSummary(this Show s) =>
        new(s.Id, s.Title, s.PosterUrl, s.FirstAirDate?.Year, s.Status, s.Network, s.Genres);

    public static EpisodeDto ToDto(this Episode e, EpisodeLabel label = EpisodeLabel.None) =>
        new(e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.Overview, e.AirsAtUtc, e.RuntimeMinutes, label);

    /// <summary>Labels premieres and finales, given all of the season's episodes.</summary>
    public static EpisodeLabel LabelFor(Episode episode, IReadOnlyCollection<Episode> seasonEpisodes)
    {
        if (episode.EpisodeNumber == 1)
        {
            return episode.SeasonNumber == 1 ? EpisodeLabel.SeriesPremiere : EpisodeLabel.SeasonPremiere;
        }

        var last = seasonEpisodes.Max(e => e.EpisodeNumber);
        return episode.EpisodeNumber == last && seasonEpisodes.Count > 1 ? EpisodeLabel.SeasonFinale : EpisodeLabel.None;
    }
}

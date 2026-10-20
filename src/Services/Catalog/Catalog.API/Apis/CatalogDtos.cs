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

public record CastMemberDto(string Name, string? Character, string? PhotoUrl);

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
    EpisodeDto? LastEpisode);

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

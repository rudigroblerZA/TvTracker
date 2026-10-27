using System.Globalization;
using Catalog.Data;
using Catalog.SyncWorker.Providers;

namespace Catalog.SyncWorker;

/// <summary>
/// Community corrections a moderator accepted (<see cref="CatalogOverride"/>). The synchronizer patches the provider's
/// show with them before comparing, so a refresh neither undoes them nor sees them as changes.
/// </summary>
public static class CatalogOverrides
{
    /// <summary>Checks a value for a field; returns the error, or null when it's fine.</summary>
    public static string? Validate(string field, string value) => field switch
    {
        _ when !CatalogOverrideFields.All.Contains(field) => $"Unknown field \"{field}\".",
        CatalogOverrideFields.EpisodeAirDate when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            => "An air date is yyyy-MM-dd.",
        CatalogOverrideFields.EpisodeRuntime when !int.TryParse(value, out var minutes) || minutes is < 1 or > 600
            => "A runtime is 1 to 600 minutes.",
        CatalogOverrideFields.EpisodeTitle when value.Trim().Length is 0 or > 300 => "A title is 1 to 300 characters.",
        _ when value.Trim().Length is 0 or > 4000 => "A summary is 1 to 4000 characters.",
        _ => null
    };

    /// <summary>The provider's show with the overrides applied; episodes are matched through their catalog ids.</summary>
    public static ProviderShow Apply(ProviderShow show, IReadOnlyList<CatalogOverride> overrides, IReadOnlyDictionary<Guid, string> episodeExternalIds)
    {
        if (overrides.Count == 0)
        {
            return show;
        }

        foreach (var o in overrides.Where(o => o.EpisodeId is null).OrderBy(o => o.CreatedAtUtc))
        {
            if (o.Field == CatalogOverrideFields.ShowSummary)
            {
                show = show with { Overview = o.Value };
            }
        }

        var byEpisode = overrides
            .Where(o => o.EpisodeId is { } id && episodeExternalIds.ContainsKey(id))
            .GroupBy(o => episodeExternalIds[o.EpisodeId!.Value])
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.CreatedAtUtc).ToList());
        if (byEpisode.Count == 0)
        {
            return show;
        }

        return show with
        {
            Episodes = show.Episodes
                .Select(e => byEpisode.TryGetValue(e.ExternalId, out var list)
                    ? list.Aggregate(e, (episode, o) => episode with
                    {
                        Title = o.Field == CatalogOverrideFields.EpisodeTitle ? o.Value : episode.Title,
                        Overview = o.Field == CatalogOverrideFields.EpisodeSummary ? o.Value : episode.Overview,
                        RuntimeMinutes = o.Field == CatalogOverrideFields.EpisodeRuntime ? int.Parse(o.Value, CultureInfo.InvariantCulture) : episode.RuntimeMinutes,
                        AirsAtUtc = o.Field == CatalogOverrideFields.EpisodeAirDate ? AirsOn(o.Value, episode.AirsAtUtc) : episode.AirsAtUtc
                    })
                    : e)
                .ToList()
        };
    }

    /// <summary>Applies one override to the stored show or episode; true when it changed anything.</summary>
    public static bool ApplyTo(Show show, CatalogOverride o)
    {
        if (o.EpisodeId is null)
        {
            if (o.Field != CatalogOverrideFields.ShowSummary || show.Overview == o.Value)
            {
                return false;
            }
            show.Overview = o.Value;
            return true;
        }

        if (show.Episodes.FirstOrDefault(e => e.Id == o.EpisodeId) is not { } episode)
        {
            return false;
        }

        switch (o.Field)
        {
            case CatalogOverrideFields.EpisodeTitle when episode.Title != o.Value:
                episode.Title = o.Value;
                return true;
            case CatalogOverrideFields.EpisodeSummary when episode.Overview != o.Value:
                episode.Overview = o.Value;
                return true;
            case CatalogOverrideFields.EpisodeRuntime when episode.RuntimeMinutes != int.Parse(o.Value, CultureInfo.InvariantCulture):
                episode.RuntimeMinutes = int.Parse(o.Value, CultureInfo.InvariantCulture);
                return true;
            case CatalogOverrideFields.EpisodeAirDate when episode.AirsAtUtc != AirsOn(o.Value, episode.AirsAtUtc):
                episode.AirsAtUtc = AirsOn(o.Value, episode.AirsAtUtc);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The corrected date at the provider's time of day (UTC midnight when it has none).</summary>
    private static DateTimeOffset AirsOn(string date, DateTimeOffset? provider) =>
        new(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.FromTimeSpan(provider?.UtcDateTime.TimeOfDay ?? TimeSpan.Zero)), TimeSpan.Zero);
}

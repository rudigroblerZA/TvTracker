using Catalog.API.Services;
using Catalog.Data;
using Catalog.Embeddings;
using Catalog.Mixes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Shows">The mixtape's shows, in the order asked.</param>
/// <param name="Indexed">False while any of them still waits to be embedded; <paramref name="Matches"/> is empty then.</param>
/// <param name="SharedGenres">Genres at least two of the shows have: what the mixtape is "about".</param>
/// <param name="Loose">
/// The shows have little in common: even the best match is farther from one of them than search's cutoff for
/// "similar" (the model's <see cref="EmbeddingModel.MaxDistance"/>). The matches are the closest common ground, not close.
/// </param>
public record MixtapeDto(IReadOnlyList<ShowSummaryDto> Shows, bool Indexed, IReadOnlyList<string> SharedGenres,
    IReadOnlyList<MixtapeMatchDto> Matches, bool Loose = false);

/// <param name="Fit">How well it fits all of them, 0-100, from its distance to the farthest.</param>
/// <param name="Links">How it relates to each of the mixtape's shows, in the same order as <see cref="MixtapeDto.Shows"/>.</param>
public record MixtapeMatchDto(ShowSummaryDto Show, int Fit, IReadOnlyList<MixtapeLinkDto> Links);

/// <param name="Closeness">0-100, from its distance to that show (100 = the same meaning).</param>
/// <param name="SharedGenres">Genres it shares with that show.</param>
public record MixtapeLinkDto(Guid ShowId, int Closeness, IReadOnlyList<string> SharedGenres);

/// <summary>
/// Mixtape: three to five shows in, the shows that fit all of them out. Blend for more than two: an even mix
/// (<see cref="ShowMixer"/>), so a show close to just one of them ranks low however close it is.
/// </summary>
public static class MixtapeApi
{
    public const int MinShows = 3;
    public const int MaxShows = 5;
    public const int MaxMatches = 24;

    public static void MapMixtapeApi(this RouteGroupBuilder api) =>
        api.MapGet("/shows/mixtape", GetMixtapeAsync).RequireRateLimiting(SearchRateLimiting.Policy).RequireFeature(FeatureFlags.Mixtape);

    /// <param name="ids">The shows, comma-separated: <see cref="MinShows"/> to <see cref="MaxShows"/> different ones.</param>
    private static async Task<Results<Ok<MixtapeDto>, NotFound, ValidationProblem>> GetMixtapeAsync(
        string? ids, CatalogContext db, EmbeddingModel model, CancellationToken ct = default)
    {
        var showIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .ToList();
        if (showIds.Contains(Guid.Empty) || showIds.Distinct().Count() != showIds.Count || showIds.Count is < MinShows or > MaxShows)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ids"] = [$"Pick {MinShows} to {MaxShows} different shows."]
            });
        }

        if (await ShowMixer.MixAsync(db, model, showIds, MixRules.EvenWeights(showIds.Count), MaxMatches, ct) is not { } mix)
        {
            return TypedResults.NotFound();
        }

        var sharedGenres = mix.Inputs.SelectMany(s => s.Genres.Distinct())
            .GroupBy(g => g)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .ToList();

        return TypedResults.Ok(new MixtapeDto(
            mix.Inputs.Select(s => s.ToSummary()).ToList(),
            mix.Indexed,
            sharedGenres,
            mix.Matches
                .Select(m => new MixtapeMatchDto(
                    m.Show.ToSummary(),
                    MixRules.Percent(m.Score),
                    mix.Inputs.Select((input, i) => new MixtapeLinkDto(input.Id, MixRules.Percent(m.Distances[i]),
                        m.Show.Genres.Intersect(input.Genres).ToList())).ToList()))
                .ToList(),
            mix.Loose));
    }
}

using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Catalog.SyncWorker;

/// <param name="Id">The correction's id in Ratings.API.</param>
public record OverrideRequest(Guid Id, Guid ShowId, Guid? EpisodeId, string Field, string Value);

/// <summary>
/// Accepted community corrections, from Ratings.API's moderation (internal, like the import API): add one, applied
/// now and on every refresh, or remove one, which lets the provider's value back on the show's next refresh.
/// </summary>
public static class OverridesApi
{
    public static void MapOverridesApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/overrides");
        api.MapPost("", AddAsync);
        api.MapDelete("/{id:guid}", RemoveAsync);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> AddAsync(
        OverrideRequest request, CatalogSynchronizer synchronizer, CancellationToken ct)
    {
        var value = request.Value?.Trim() ?? "";
        if (CatalogOverrides.Validate(request.Field, value) is { } error)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["value"] = [error] });
        }
        if (CatalogOverrideFields.IsEpisodeField(request.Field) != request.EpisodeId.HasValue)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["field"] = ["That field belongs to the other kind (show or episode)."] });
        }

        var added = await synchronizer.AddOverrideAsync(new CatalogOverride
        {
            Id = request.Id,
            ShowId = request.ShowId,
            EpisodeId = request.EpisodeId,
            Field = request.Field,
            Value = value
        }, ct);
        return added ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveAsync(Guid id, CatalogSynchronizer synchronizer, CancellationToken ct) =>
        await synchronizer.RemoveOverrideAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();
}

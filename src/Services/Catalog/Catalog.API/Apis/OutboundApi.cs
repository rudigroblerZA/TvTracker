using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.API.Apis;

public record OutboundRequest(Guid ShowId, string Provider, WatchProviderType Type, string Region, string Surface);

/// <param name="Program">The affiliate program the link goes through; null for a plain link.</param>
public record OutboundDto(string Url, Guid ClickId, string? Program);

/// <summary>
/// Where-to-watch clicks (the <c>affiliates</c> flag): says where to send the viewer, a link the show already lists,
/// wrapped by an affiliate program when one covers the service and region.
/// <list type="bullet">
/// <item><b>The programs are Financials.API's;</b> the link is built from Catalog's copy of them
/// (<see cref="CopiedAffiliatePrograms"/>), so a click doesn't wait on another service.</item>
/// <item><b>The click is Financials' to keep:</b> it's published (<see cref="OutboundClickedIntegrationEvent"/>) under the id
/// the link carries, and a sale the network reports later is matched to it there.</item>
/// <item><b>Only what's listed:</b> the link is one of the show's own where-to-watch listings, never an address the
/// caller gives.</item>
/// </list>
/// </summary>
public static class OutboundApi
{
    private const string SignedOut = "signed-out";
    private const string UnknownPlan = "unknown";

    public static void MapOutboundApi(this RouteGroupBuilder api) =>
        api.MapPost("/outbound", OutboundAsync)
            .RequireRateLimiting(SearchRateLimiting.Policy)
            .RequireFeature(FeatureFlags.Affiliates);

    private static async Task<Results<Ok<OutboundDto>, NotFound, ValidationProblem>> OutboundAsync(OutboundRequest request, HttpContext http,
        CatalogContext db, CopiedAffiliatePrograms programs, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        if (!OutboundSurfaces.All.Contains(request.Surface) || request.Region is not { Length: 2 } || request.Provider is not { Length: > 0 and <= 128 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [$"Give a provider, a two-letter region and a surface ({string.Join(", ", OutboundSurfaces.All)})."]
            });
        }

        var region = request.Region.ToUpperInvariant();
        var listing = await db.WatchProviders.AsNoTracking()
            .Where(w => w.ShowId == request.ShowId && w.Region == region && w.ProviderName == request.Provider && w.Type == request.Type)
            .Join(db.Shows, w => w.ShowId, s => s.Id, (w, s) => new { Provider = w, s.Title })
            .FirstOrDefaultAsync(ct);
        if (listing is null || CatalogApi.WatchLinkFor(listing.Provider, listing.Title) is not { Url: { } url, Link: { } link })
        {
            return TypedResults.NotFound();
        }

        var clickId = Guid.CreateVersion7();
        var service = listing.Provider.ProviderName;
        // JustWatch's links are its own attributed ones and go out untouched: counted, never through a program.
        var program = listing.Provider.Source == WatchSource.JustWatch ? null : AffiliateLinks.For(await programs.AllAsync(ct), service, region);
        outbox.Add(new OutboundClickedIntegrationEvent(clickId, request.ShowId, listing.Title, service, request.Type.ToString(), link.ToString(), region,
            request.Surface, await PlanAsync(http), program?.Name, time.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        return TypedResults.Ok(new OutboundDto(program is null ? url : AffiliateLinks.Wrap(program, url, clickId), clickId, program?.Name));
    }

    private static async Task<string> PlanAsync(HttpContext http) =>
        http.User.Identity?.IsAuthenticated != true ? SignedOut
        : http.RequestServices.GetService<IUserPlans>() is { } plans ? await plans.PlanAsync(http) ?? UnknownPlan
        : UnknownPlan;
}

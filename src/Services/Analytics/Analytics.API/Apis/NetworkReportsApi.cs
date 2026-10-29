using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>
/// Network report cards, as stored hourly by Analytics.Worker (<see cref="NetworkReport"/> JSON): every network with
/// enough catalog shows, most shows first, and the whole catalog as the baseline to compare with.
/// </summary>
public static class NetworkReportsApi
{
    public static void MapNetworkReportsApi(this RouteGroupBuilder api) =>
        api.MapGet("/insights/networks", GetReportsAsync).RequireFeature(FeatureFlags.NetworkReports);

    /// <summary><c>{ "baseline": NetworkReport | null, "networks": NetworkReport[] }</c>, the documents as stored.</summary>
    private static async Task<ContentHttpResult> GetReportsAsync(AnalyticsContext db, CancellationToken ct)
    {
        var rows = await db.NetworkReports.AsNoTracking()
            .OrderByDescending(r => r.Shows).ThenBy(r => r.Network)
            .Select(r => new { r.Network, r.Document })
            .ToListAsync(ct);
        var baseline = rows.FirstOrDefault(r => r.Network == NetworkReports.Catalog)?.Document ?? "null";
        var networks = string.Join(',', rows.Where(r => r.Network != NetworkReports.Catalog).Select(r => r.Document));
        return TypedResults.Text($$"""{"baseline":{{baseline}},"networks":[{{networks}}]}""", "application/json");
    }
}

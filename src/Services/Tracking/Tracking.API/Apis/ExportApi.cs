using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Tracking.API.Data;

namespace Tracking.API.Apis;

public static class ExportApi
{
    /// <summary>The user's tracking data, collected by Identity.API's account export.</summary>
    public static IEndpointRouteBuilder MapExportApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me/export", async (ClaimsPrincipal user, TrackingContext db, CancellationToken ct) =>
        {
            var userId = user.GetUserId();

            var follows = await db.Follows.AsNoTracking().Where(f => f.UserId == userId)
                .Select(f => new { f.ShowId, Status = f.Status.ToString(), f.FollowedAtUtc })
                .ToListAsync(ct);

            var watched = await db.WatchedEpisodes.AsNoTracking().Where(w => w.UserId == userId)
                .OrderBy(w => w.WatchedAtUtc)
                .Select(w => new { w.ShowId, w.EpisodeId, w.WatchedAtUtc })
                .ToListAsync(ct);

            var lists = await db.Lists.AsNoTracking().Where(l => l.UserId == userId)
                .Select(l => new { l.Name, l.Description, Shows = l.Items.OrderBy(i => i.Position).Select(i => i.ShowId).ToList() })
                .ToListAsync(ct);

            return Results.Ok(new { follows, watched, lists });
        }).RequireAuthorization();

        return app;
    }
}

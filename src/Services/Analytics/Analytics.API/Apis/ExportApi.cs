using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Analytics.API.Apis;

/// <summary>What the user gave Analytics directly (everything else here is a copy of other services' data).</summary>
public record AnalyticsExportDto(IReadOnlyList<ExportedServiceDto> Services, IReadOnlyList<ExportedCallDto> SeasonCalls);

public record ExportedServiceDto(string ServiceKey, DateTime AddedAtUtc);

public record ExportedCallDto(Guid ShowId, int SeasonNumber, double Rating, DateTime CalledAtUtc, double? Actual);

/// <summary>The data export Identity gathers from every service: the user's picked services and season calls.</summary>
public static class ExportApi
{
    public static void MapExportApi(this RouteGroupBuilder api) => api.MapGet("/me/export", GetAsync);

    private static async Task<Ok<AnalyticsExportDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return TypedResults.Ok(new AnalyticsExportDto(
            await db.UserServices.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new ExportedServiceDto(s.ServiceKey, s.AddedAtUtc)).ToListAsync(ct),
            await db.SeasonCalls.AsNoTracking().Where(c => c.UserId == userId)
                .Select(c => new ExportedCallDto(c.ShowId, c.SeasonNumber, c.Rating, c.CalledAtUtc, c.Actual)).ToListAsync(ct)));
    }
}

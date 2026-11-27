using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Analytics.API.Apis;

/// <summary>
/// What the user gave Analytics directly (everything else here is a copy of other services' data).
/// </summary>
public record AnalyticsExportDto(IReadOnlyList<ExportedServiceDto> Services, IReadOnlyList<ExportedCallDto> SeasonCalls,
    IReadOnlyList<ExportedMatchDto> TasteMatches);

public record ExportedServiceDto(string ServiceKey, DateTime AddedAtUtc);

public record ExportedCallDto(Guid ShowId, int SeasonNumber, double Rating, DateTime CalledAtUtc, double? Actual);

/// <param name="Role">"inviter" or "friend".</param>
/// <param name="Unnamed">Shows the user asked not to have named in the result.</param>
/// <param name="Result">The result, once the friend had answered.</param>
public record ExportedMatchDto(string Token, string Role, string? YourName, string? OtherName, IReadOnlyList<Guid> Unnamed, DateTime CreatedAtUtc,
    DateTime? DoneAtUtc, DateTime ExpiresAtUtc, TasteMatchDto? Result);

/// <summary>
/// The data export Identity gathers from every service: the user's picked services, season calls and taste matches.
/// </summary>
public static class ExportApi
{
    public static void MapExportApi(this RouteGroupBuilder api) => api.MapGet("/me/export", GetAsync);

    internal static async Task<Ok<AnalyticsExportDto>> GetAsync(ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return TypedResults.Ok(new AnalyticsExportDto(
            await db.UserServices.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new ExportedServiceDto(s.ServiceKey, s.AddedAtUtc)).ToListAsync(ct),
            await db.SeasonCalls.AsNoTracking().Where(c => c.UserId == userId)
                .Select(c => new ExportedCallDto(c.ShowId, c.SeasonNumber, c.Rating, c.CalledAtUtc, c.Actual)).ToListAsync(ct),
            (await db.TasteMatches.AsNoTracking().Where(m => m.InviterUserId == userId || m.FriendUserId == userId).ToListAsync(ct))
                .Select(m => m.InviterUserId == userId
                    ? new ExportedMatchDto(m.Token, "inviter", m.InviterDisplayName, m.FriendDisplayName, m.InviterUnnamed, m.CreatedAtUtc, m.DoneAtUtc,
                        m.ExpiresAtUtc, TasteMatchApi.ResultOf(m))
                    : new ExportedMatchDto(m.Token, "friend", m.FriendDisplayName, m.InviterDisplayName, m.FriendUnnamed, m.CreatedAtUtc, m.DoneAtUtc,
                        m.ExpiresAtUtc, TasteMatchApi.ResultOf(m)))
                .ToList()));
    }
}

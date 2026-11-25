using System.Security.Claims;
using Identity.API.Services;
using Identity.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Apis;

/// <param name="Plan">The plan the user gets (<see cref="Plans"/>): their own, or the family plan they're in.</param>
/// <param name="Purchased">The plan they pay for themselves.</param>
/// <param name="MaxLists">The plan's list limit; null for none.</param>
public record PlanDto(string Plan, string Name, int? MaxLists, bool ShareLists, DateTime? ChangedAtUtc, string Purchased = Plans.Free,
    bool TrackServices = false);

public record PlanUserDto(string UserId, string DisplayName, string? Email, string Plan, DateTime? PlanChangedAtUtc);

public record SetPlanRequest(string Plan);

/// <param name="NewAccounts">When each account made in the window was made.</param>
public record AccountsDto(int Accounts, int OnPaidPlan, IReadOnlyList<DateTime> NewAccounts);

/// <summary>
/// Plans (<see cref="Plans"/>): what a user pays for. Identity owns each user's plan; a change goes out as
/// <see cref="UserPlanChangedIntegrationEvent"/> through the outbox, and every plan rides the daily profile snapshot, so
/// services enforcing a limit keep a copy that heals. People buy plans through Financials.API, which says when a subscription starts or ends
/// (<see cref="IntegrationEvents.SubscriptionPlanHandlers"/>),
/// and admins (<c>Admin:Users</c>) can still set one; both make the same change (<see cref="PlanState.SetPaidPlanAsync"/>).
/// Non-admins get 404 from the admin endpoints.
/// </summary>
public static class PlansApi
{
    private const int MaxAccountDays = 365;

    private const int MaxSearchResults = 20;

    public static void MapPlansApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireFeature(FeatureFlags.Plans);

        api.MapGet("/me/plan", GetMyPlanAsync);
        api.MapGet("/admin/plans/users", SearchUsersAsync);
        api.MapPut("/admin/plans/users/{userId}", SetPlanAsync);
        api.MapGet("/admin/accounts", AccountsAsync);
    }

    private static async Task<Ok<PlanDto>> GetMyPlanAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var profile = await ProfileApi.GetOrCreateProfileAsync(user, db, outbox, time, ct);
        return TypedResults.Ok(ToDto(profile));
    }

    /// <summary>
    /// Users whose name or email contains <paramref name="q"/>, Plus users first; with no term, every Plus user.
    /// </summary>
    private static async Task<Results<Ok<List<PlanUserDto>>, NotFound>> SearchUsersAsync(
        string? q, ClaimsPrincipal user, IConfiguration config, IdentityContext db, CancellationToken ct)
    {
        if (!AppAdmins.Is(user, config))
        {
            return TypedResults.NotFound();
        }

        var term = q?.Trim();
        var query = db.Profiles.AsNoTracking();
        query = string.IsNullOrEmpty(term)
            ? query.Where(p => p.Plan != Plans.Free)
            : query.Where(p => EF.Functions.ILike(p.DisplayName, $"%{term}%") || (p.Email != null && EF.Functions.ILike(p.Email, $"%{term}%")));
        var users = await query
            .OrderBy(p => p.Plan == Plans.Free).ThenBy(p => p.DisplayName)
            .Take(MaxSearchResults)
            .Select(p => new PlanUserDto(p.UserId, p.DisplayName, p.Email, p.Plan, p.PlanChangedAtUtc))
            .ToListAsync(ct);
        return TypedResults.Ok(users);
    }

    private static async Task<Results<Ok<PlanUserDto>, NotFound, ValidationProblem>> SetPlanAsync(
        string userId, SetPlanRequest request, ClaimsPrincipal user, IConfiguration config, IdentityContext db,
        IIntegrationEventOutbox outbox, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!AppAdmins.Is(user, config))
        {
            return TypedResults.NotFound();
        }
        if (!Plans.All.Contains(request.Plan))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["plan"] = [$"Plan must be one of: {string.Join(", ", Plans.All)}."] });
        }
        if (await db.Profiles.FindAsync([userId], ct) is not { } profile)
        {
            return TypedResults.NotFound();
        }

        if (await PlanState.SetPaidPlanAsync(db, outbox, profile, request.Plan, time.GetUtcNow(), ct))
        {
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
            loggers.CreateLogger(typeof(PlansApi)).LogInformation("Plan of {UserId} set to {Plan} by {Admin}",
                profile.UserId, profile.Plan, user.FindFirstValue("preferred_username"));
        }

        return TypedResults.Ok(new PlanUserDto(profile.UserId, profile.DisplayName, profile.Email, profile.Plan, profile.PlanChangedAtUtc));
    }

    /// <summary>
    /// How many accounts there are, how many are on a paid plan (their own or a family's), and when each made in the last
    /// <paramref name="days"/> days was made: Financials.API's revenue report sets its money against these.
    /// </summary>
    private static async Task<Results<Ok<AccountsDto>, NotFound>> AccountsAsync(ClaimsPrincipal user, IConfiguration config, IdentityContext db,
        TimeProvider time, int days = 30, CancellationToken ct = default)
    {
        if (!AppAdmins.Is(user, config))
        {
            return TypedResults.NotFound();
        }
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var start = today.AddDays(-(Math.Clamp(days, 1, MaxAccountDays) - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return TypedResults.Ok(new AccountsDto(
            await db.Profiles.CountAsync(ct),
            await db.Profiles.CountAsync(p => p.EffectivePlan != Plans.Free, ct),
            await db.Profiles.Where(p => p.CreatedAtUtc >= start).Select(p => p.CreatedAtUtc).ToListAsync(ct)));
    }

    private static PlanDto ToDto(UserProfile profile)
    {
        var limits = Plans.Limits(profile.EffectivePlan);
        return new PlanDto(profile.EffectivePlan, Plans.Name(profile.EffectivePlan), limits.MaxLists, limits.ShareLists,
            profile.EffectivePlanChangedAtUtc, profile.Plan, limits.TrackServices);
    }
}

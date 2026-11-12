using System.Security.Claims;
using Identity.API.Data;
using Identity.API.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Identity.API.Apis;

public record RevenueMoneyDto(string Currency, decimal Amount);

/// <param name="Plan">"plus" or "family".</param>
/// <param name="Interval">"month" or "year".</param>
/// <param name="Cancelling">Of <paramref name="Active"/>, those set to end with the period they've paid for.</param>
public record RevenuePlanRowDto(string Plan, string Name, string Interval, int Active, int Cancelling);

public record RevenueDayDto(DateOnly Day, int NewAccounts, int Payments, int NewSubscriptions);

/// <summary>
/// What subscriptions bring in, for admins: who pays, how many free accounts become paying ones, and the money received.
/// </summary>
/// <param name="Accounts">Everyone registered.</param>
/// <param name="NewAccounts">Registered in the window.</param>
/// <param name="PayingAccounts">Accounts with a running subscription of their own.</param>
/// <param name="OnPaidPlan">Accounts whose plan is a paid one, however they came by it: their own subscription, a family they're in, or an admin.</param>
/// <param name="PaidShare">Paying accounts of all accounts, 0 to 1: the free-to-paid rate.</param>
/// <param name="MonthlyRecurring">What the running subscriptions come to a month (a yearly one counts a twelfth), per currency. Ones set to end are still in it until they do.</param>
/// <param name="Received">Payments received in the window, per currency.</param>
/// <param name="CheckoutsStarted">Checkouts begun in the window: someone pressed a price on the plan page.</param>
/// <param name="CheckoutsCompleted">Of those, the ones paid.</param>
/// <param name="NewSubscriptions">Subscriptions that started in the window.</param>
/// <param name="Ended">Subscriptions that ended in the window.</param>
public record RevenueReportDto(DateOnly From, DateOnly To, int Accounts, int NewAccounts, int PayingAccounts, int OnPaidPlan, double PaidShare,
    IReadOnlyList<RevenueMoneyDto> MonthlyRecurring, IReadOnlyList<RevenueMoneyDto> Received, int CheckoutsStarted, int CheckoutsCompleted,
    int NewSubscriptions, int Ended, IReadOnlyList<RevenuePlanRowDto> Plans, IReadOnlyList<RevenueDayDto> Days);

/// <summary>
/// The subscription side of revenue, for the admins' Revenue page (the affiliate side is Catalog.API's report).
/// </summary>
public static class RevenueApi
{
    private const int MaxReportDays = 365;

    public static void MapRevenueApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/admin/revenue", ReportAsync).RequireAuthorization();

    private static async Task<Results<Ok<RevenueReportDto>, NotFound>> ReportAsync(ClaimsPrincipal user, IConfiguration config, IdentityContext db,
        TimeProvider time, int days = 30, CancellationToken ct = default)
    {
        if (!FlagsApi.IsAdmin(user, config))
        {
            return TypedResults.NotFound();
        }

        var to = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-(Math.Clamp(days, 1, MaxReportDays) - 1));
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var accounts = await db.Profiles.CountAsync(ct);
        var onPaidPlan = await db.Profiles.CountAsync(p => p.EffectivePlan != Plans.Free, ct);
        var newAccounts = await db.Profiles.Where(p => p.CreatedAtUtc >= start).Select(p => p.CreatedAtUtc).ToListAsync(ct);
        var active = await db.Subscriptions.AsNoTracking().Where(s => s.Status == SubscriptionStatus.Active)
            .Select(s => new ActiveSubscription(s.UserId, s.Plan, s.Interval, s.Amount, s.Currency, s.CancelAtPeriodEnd))
            .ToListAsync(ct);
        var started = await db.Subscriptions.Where(s => s.CreatedAtUtc >= start).Select(s => s.CreatedAtUtc).ToListAsync(ct);
        var ended = await db.Subscriptions.CountAsync(s => s.EndedAtUtc != null && s.EndedAtUtc >= start, ct);
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.PaidAtUtc >= start)
            .Select(r => new Receipt(r.PaidAtUtc, r.Amount, r.Currency))
            .ToListAsync(ct);
        var checkouts = await db.CheckoutSessions.AsNoTracking().Where(c => c.CreatedAtUtc >= start)
            .Select(c => c.CompletedAtUtc != null)
            .ToListAsync(ct);

        return TypedResults.Ok(RevenueReport.Build(from, to, accounts, onPaidPlan, newAccounts, active, started, ended, receipts,
            checkouts.Count, checkouts.Count(completed => completed)));
    }
}

public sealed record ActiveSubscription(string UserId, string Plan, string Interval, decimal Amount, string Currency, bool CancelAtPeriodEnd);

public sealed record Receipt(DateTime PaidAtUtc, decimal Amount, string Currency);

/// <summary>
/// The revenue report from the rows read for it; pure, so its sums are tested without a database.
/// </summary>
public static class RevenueReport
{
    public static RevenueReportDto Build(DateOnly from, DateOnly to, int accounts, int onPaidPlan, IReadOnlyList<DateTime> newAccounts,
        IReadOnlyList<ActiveSubscription> active, IReadOnlyList<DateTime> started, int ended, IReadOnlyList<Receipt> receipts,
        int checkoutsStarted, int checkoutsCompleted)
    {
        var paying = active.Select(s => s.UserId).Distinct().Count();
        var monthly = active.GroupBy(s => s.Currency.ToUpperInvariant())
            .Select(g => new RevenueMoneyDto(g.Key, Math.Round(g.Sum(s => s.Interval == PlanPrices.Year ? s.Amount / 12 : s.Amount), 2)))
            .OrderBy(m => m.Currency, StringComparer.Ordinal)
            .ToList();
        var received = receipts.GroupBy(r => r.Currency.ToUpperInvariant())
            .Select(g => new RevenueMoneyDto(g.Key, g.Sum(r => r.Amount)))
            .OrderBy(m => m.Currency, StringComparer.Ordinal)
            .ToList();
        var planOrder = Plans.All.ToList();
        var plans = active.GroupBy(s => (s.Plan, s.Interval))
            .Select(g => new RevenuePlanRowDto(g.Key.Plan, Plans.Name(g.Key.Plan), g.Key.Interval, g.Count(), g.Count(s => s.CancelAtPeriodEnd)))
            .OrderBy(r => planOrder.IndexOf(r.Plan)).ThenBy(r => r.Interval, StringComparer.Ordinal)
            .ToList();
        // One pass over each list, not one per day.
        var accountsOn = PerDay(newAccounts);
        var paymentsOn = PerDay(receipts.Select(r => r.PaidAtUtc));
        var startedOn = PerDay(started);
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays)
            .Select(d => new RevenueDayDto(d, accountsOn.GetValueOrDefault(d), paymentsOn.GetValueOrDefault(d), startedOn.GetValueOrDefault(d)))
            .ToList();
        return new RevenueReportDto(from, to, accounts, newAccounts.Count, paying, onPaidPlan,
            accounts == 0 ? 0 : Math.Round((double)paying / accounts, 4), monthly, received, checkoutsStarted, checkoutsCompleted, started.Count,
            ended, plans, days);
    }

    private static Dictionary<DateOnly, int> PerDay(IEnumerable<DateTime> times) =>
        times.GroupBy(DateOnly.FromDateTime).ToDictionary(g => g.Key, g => g.Count());
}

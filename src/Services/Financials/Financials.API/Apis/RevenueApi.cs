using Financials.API.Payments;
using Financials.API.Services;
using Financials.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Financials.API.Apis;

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
    IReadOnlyList<Money> MonthlyRecurring, IReadOnlyList<Money> Received, int CheckoutsStarted, int CheckoutsCompleted,
    int NewSubscriptions, int Ended, IReadOnlyList<RevenuePlanRowDto> Plans, IReadOnlyList<RevenueDayDto> Days);

/// <summary>
/// The subscription side of revenue, for the admins' Revenue page (the affiliate side is <see cref="AffiliateApi"/>'s report).
/// </summary>
public static class RevenueApi
{
    private const int MaxReportDays = 365;

    public static void MapRevenueApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/admin/revenue", ReportAsync).RequireAuthorization().RequireAppAdmin();

    /// <summary>
    /// What subscriptions brought in since <paramref name="start"/>, by month, and what the running ones bring in a
    /// month now: the financial statement's subscription side.
    /// </summary>
    internal static async Task<SubscriptionIncome> IncomeAsync(FinancialsContext db, DateTime start, CancellationToken ct) =>
        new(RevenueReport.ByMonth(await ReceiptsSinceAsync(db, start, ct)), RevenueReport.MonthlyRecurring(await ActiveAsync(db, ct)));

    private static Task<List<Receipt>> ReceiptsSinceAsync(FinancialsContext db, DateTime start, CancellationToken ct) =>
        db.Receipts.AsNoTracking().Where(r => r.PaidAtUtc >= start)
            .Select(r => new Receipt(r.PaidAtUtc, r.Amount, r.Currency))
            .ToListAsync(ct);

    private static Task<List<ActiveSubscription>> ActiveAsync(FinancialsContext db, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking().Where(s => s.Status == SubscriptionStatus.Active)
            .Select(s => new ActiveSubscription(s.UserId, s.Plan, s.Interval, s.Amount, s.Currency, s.CancelAtPeriodEnd))
            .ToListAsync(ct);

    private static async Task<Ok<RevenueReportDto>> ReportAsync(HttpContext http, FinancialsContext db, Members members, TimeProvider time,
        int days = 30, CancellationToken ct = default)
    {
        var to = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-(Math.Clamp(days, 1, MaxReportDays) - 1));
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // The accounts are Identity's to count; without its answer the report shows the money and no accounts.
        var summary = await members.AccountsAsync(http.Request.Headers.Authorization.ToString(), to.DayNumber - from.DayNumber + 1, ct);
        var (accounts, onPaidPlan, newAccounts) = (summary?.Accounts ?? 0, summary?.OnPaidPlan ?? 0, summary?.NewAccounts ?? []);
        var active = await ActiveAsync(db, ct);
        var started = await db.Subscriptions.Where(s => s.CreatedAtUtc >= start).Select(s => s.CreatedAtUtc).ToListAsync(ct);
        var ended = await db.Subscriptions.CountAsync(s => s.EndedAtUtc != null && s.EndedAtUtc >= start, ct);
        var receipts = await ReceiptsSinceAsync(db, start, ct);
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
        var monthly = MonthlyRecurring(active);
        var received = receipts.GroupBy(r => r.Currency.ToUpperInvariant())
            .Select(g => new Money(g.Key, g.Sum(r => r.Amount)))
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

    /// <summary>
    /// What the running subscriptions bring in a month, by currency; a yearly plan counts as a twelfth.
    /// </summary>
    public static List<Money> MonthlyRecurring(IReadOnlyList<ActiveSubscription> active) =>
        active.GroupBy(s => s.Currency.ToUpperInvariant())
            .Select(g => new Money(g.Key, Math.Round(g.Sum(s => s.Interval == PlanPrices.Year ? s.Amount / 12 : s.Amount), 2)))
            .OrderBy(m => m.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Payments by the month they were received in (UTC) and currency, newest month first.
    /// </summary>
    public static List<IncomeMonth> ByMonth(IReadOnlyList<Receipt> receipts) =>
        receipts.GroupBy(r => (Month: new DateOnly(r.PaidAtUtc.Year, r.PaidAtUtc.Month, 1), Currency: r.Currency.ToUpperInvariant()))
            .Select(g => new IncomeMonth(g.Key.Month, g.Key.Currency, g.Sum(r => r.Amount), g.Count()))
            .OrderByDescending(m => m.Month).ThenBy(m => m.Currency, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<DateOnly, int> PerDay(IEnumerable<DateTime> times) =>
        times.GroupBy(DateOnly.FromDateTime).ToDictionary(g => g.Key, g => g.Count());
}

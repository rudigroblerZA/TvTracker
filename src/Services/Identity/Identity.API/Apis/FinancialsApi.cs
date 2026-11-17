using System.Security.Claims;
using System.Text.RegularExpressions;
using Identity.API.Data;
using Identity.API.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Identity.API.Apis;

/// <param name="Recurrence">"monthly", "yearly" or "once".</param>
public record CostItemDto(Guid Id, string Name, string Category, decimal Amount, string Currency, string Recurrence, DateOnly StartsOn,
    DateOnly? EndsOn, string? Notes);

public record CostItemRequest(string? Name, string? Category, decimal Amount, string? Currency, string? Recurrence, DateOnly StartsOn,
    DateOnly? EndsOn, string? Notes);

public record FinancialCategoryDto(string Category, decimal Amount);

/// <summary>
/// One month in one currency: what subscriptions brought in, what taking those payments is estimated to have cost,
/// and the costs entered for it.
/// </summary>
/// <param name="Month">The month's first day.</param>
/// <param name="Payments">How many payments make up <paramref name="Subscriptions"/>.</param>
/// <param name="PaymentFees">An estimate (<see cref="FinancialOptions"/>), not the provider's bill.</param>
public record FinancialMonthDto(DateOnly Month, string Currency, decimal Subscriptions, int Payments, decimal PaymentFees, decimal Costs,
    IReadOnlyList<FinancialCategoryDto> CostsByCategory);

/// <summary>
/// The business's own books for the last months: subscription income, estimated payment fees and entered costs, by
/// month and currency. Affiliate commission is Catalog's to report (<c>/api/v1/affiliates/monthly</c>); the page puts
/// the two together.
/// </summary>
/// <param name="Months">Newest month first; only months and currencies with something in them.</param>
/// <param name="MonthlyRecurring">What the running subscriptions bring in a month, now.</param>
/// <param name="MonthlyCosts">What the costs running today come to a month, a yearly one counted as a twelfth.</param>
public record FinancialStatementDto(DateOnly From, DateOnly To, decimal FeePercent, decimal FeeFixed, IReadOnlyList<FinancialMonthDto> Months,
    IReadOnlyList<RevenueMoneyDto> MonthlyRecurring, IReadOnlyList<RevenueMoneyDto> MonthlyCosts, IReadOnlyList<string> Categories);

/// <summary>
/// What the payment provider is assumed to keep of each payment, for the financials' fee estimate (<c>Financials</c>).
/// The defaults are Stripe's standard card rate; set them to the rate on the provider's own statement.
/// </summary>
public sealed class FinancialOptions
{
    /// <summary>
    /// Percent of each payment.
    /// </summary>
    public decimal PaymentFeePercent { get; set; } = 2.9m;

    /// <summary>
    /// Added per payment, in the payment's own currency.
    /// </summary>
    public decimal PaymentFeeFixed { get; set; } = 0.30m;
}

/// <summary>
/// The admin's financials: the costs they enter, and the statement made from those and the payments received. Admins
/// only; anyone else gets 404, as with the other admin pages.
/// </summary>
public static class FinancialsApi
{
    public static void MapFinancialsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/admin/financials").RequireAuthorization();
        api.MapGet("/", StatementAsync);
        api.MapGet("/costs", CostsAsync);
        api.MapPost("/costs", AddCostAsync);
        api.MapPut("/costs/{id:guid}", SaveCostAsync);
        api.MapDelete("/costs/{id:guid}", DeleteCostAsync);
    }

    private static async Task<Results<Ok<FinancialStatementDto>, NotFound>> StatementAsync(ClaimsPrincipal user, IConfiguration config,
        IdentityContext db, IOptions<FinancialOptions> options, TimeProvider time, int months = 12, CancellationToken ct = default)
    {
        if (!FlagsApi.IsAdmin(user, config))
        {
            return TypedResults.NotFound();
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var from = FinancialStatement.FirstMonth(today, months);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.PaidAtUtc >= start)
            .Select(r => new Receipt(r.PaidAtUtc, r.Amount, r.Currency))
            .ToListAsync(ct);
        var costs = await db.CostItems.AsNoTracking().ToListAsync(ct);
        var active = await db.Subscriptions.AsNoTracking().Where(s => s.Status == SubscriptionStatus.Active)
            .Select(s => new ActiveSubscription(s.UserId, s.Plan, s.Interval, s.Amount, s.Currency, s.CancelAtPeriodEnd))
            .ToListAsync(ct);
        return TypedResults.Ok(FinancialStatement.Build(today, months, receipts, costs, active, options.Value));
    }

    private static async Task<Results<Ok<List<CostItemDto>>, NotFound>> CostsAsync(ClaimsPrincipal user, IConfiguration config, IdentityContext db,
        CancellationToken ct) =>
        !FlagsApi.IsAdmin(user, config)
            ? TypedResults.NotFound()
            : TypedResults.Ok((await db.CostItems.AsNoTracking().OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync(ct)).Select(ToDto).ToList());

    private static async Task<Results<Ok<CostItemDto>, NotFound, ValidationProblem>> AddCostAsync(CostItemRequest request, ClaimsPrincipal user,
        IConfiguration config, IdentityContext db, TimeProvider time, CancellationToken ct)
    {
        if (!FlagsApi.IsAdmin(user, config))
        {
            return TypedResults.NotFound();
        }
        if (FinancialStatement.Problem(request) is { } problem)
        {
            return Invalid(problem);
        }
        var item = new CostItem { Id = Guid.CreateVersion7(), Name = "", Category = "", Currency = "" };
        FinancialStatement.Apply(request, item, time.GetUtcNow().UtcDateTime);
        db.CostItems.Add(item);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Results<Ok<CostItemDto>, NotFound, ValidationProblem>> SaveCostAsync(Guid id, CostItemRequest request,
        ClaimsPrincipal user, IConfiguration config, IdentityContext db, TimeProvider time, CancellationToken ct)
    {
        if (!FlagsApi.IsAdmin(user, config) || await db.CostItems.FindAsync([id], ct) is not { } item)
        {
            return TypedResults.NotFound();
        }
        if (FinancialStatement.Problem(request) is { } problem)
        {
            return Invalid(problem);
        }
        FinancialStatement.Apply(request, item, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteCostAsync(Guid id, ClaimsPrincipal user, IConfiguration config,
        IdentityContext db, CancellationToken ct)
    {
        if (!FlagsApi.IsAdmin(user, config))
        {
            return TypedResults.NotFound();
        }
        // Gone already is as good as deleted.
        await db.CostItems.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static ValidationProblem Invalid(string problem) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cost"] = [problem] });

    private static CostItemDto ToDto(CostItem c) =>
        new(c.Id, c.Name, c.Category, c.Amount, c.Currency, c.Recurrence.ToString().ToLowerInvariant(), c.StartsOn, c.EndsOn, c.Notes);
}

/// <summary>
/// The financial statement's rules, apart from the database: which months it covers, what a cost comes to in a month,
/// and the fee estimate.
/// </summary>
public static partial class FinancialStatement
{
    public const int MaxMonths = 36;

    /// <summary>
    /// What a cost can be filed under, in the order the page lists them.
    /// </summary>
    public static readonly IReadOnlyList<string> Categories = ["hosting", "data", "services", "payments", "marketing", "legal", "people", "other"];

    /// <summary>
    /// The first day of the earliest month a statement of <paramref name="months"/> months covers, this month included.
    /// </summary>
    public static DateOnly FirstMonth(DateOnly today, int months) =>
        new DateOnly(today.Year, today.Month, 1).AddMonths(-(Math.Clamp(months, 1, MaxMonths) - 1));

    public static FinancialStatementDto Build(DateOnly today, int months, IReadOnlyList<Receipt> receipts, IReadOnlyList<CostItem> costs,
        IReadOnlyList<ActiveSubscription> active, FinancialOptions fees)
    {
        var from = FirstMonth(today, months);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var rows = new List<FinancialMonthDto>();
        for (var month = thisMonth; month >= from; month = month.AddMonths(-1))
        {
            var paid = receipts.Where(r => DateOnly.FromDateTime(r.PaidAtUtc) is var day && day.Year == month.Year && day.Month == month.Month)
                .GroupBy(r => r.Currency.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => (Amount: g.Sum(r => r.Amount), Count: g.Count()));
            var spent = costs.Select(c => (c.Currency, c.Category, Amount: InMonth(c, month))).Where(c => c.Amount > 0)
                .GroupBy(c => c.Currency)
                .ToDictionary(g => g.Key, g => g.GroupBy(c => c.Category)
                    .Select(byCategory => new FinancialCategoryDto(byCategory.Key, byCategory.Sum(c => c.Amount)))
                    .OrderBy(c => Categories.IndexOf(c.Category))
                    .ToList());
            foreach (var currency in paid.Keys.Union(spent.Keys).Order(StringComparer.Ordinal))
            {
                var (amount, count) = paid.GetValueOrDefault(currency);
                var byCategory = spent.GetValueOrDefault(currency) ?? [];
                rows.Add(new FinancialMonthDto(month, currency, amount, count, Fee(amount, count, fees), byCategory.Sum(c => c.Amount), byCategory));
            }
        }

        var recurring = active.GroupBy(s => s.Currency.ToUpperInvariant())
            .Select(g => new RevenueMoneyDto(g.Key, Math.Round(g.Sum(s => s.Interval == PlanPrices.Year ? s.Amount / 12 : s.Amount), 2)))
            .OrderBy(m => m.Currency, StringComparer.Ordinal)
            .ToList();
        var running = costs.Where(c => c.Recurrence != CostRecurrence.Once && c.StartsOn <= today && (c.EndsOn is null || c.EndsOn >= today))
            .GroupBy(c => c.Currency)
            .Select(g => new RevenueMoneyDto(g.Key, Math.Round(g.Sum(c => c.Recurrence == CostRecurrence.Yearly ? c.Amount / 12 : c.Amount), 2)))
            .OrderBy(m => m.Currency, StringComparer.Ordinal)
            .ToList();
        return new FinancialStatementDto(from, today, fees.PaymentFeePercent, fees.PaymentFeeFixed, rows, recurring, running, Categories);
    }

    /// <summary>
    /// What a cost comes to in the month starting <paramref name="month"/>.
    /// <list type="bullet">
    /// <item><b>Once:</b> all of it, in the month it's paid.</item>
    /// <item><b>Monthly:</b> the amount, in every month it runs for any part of.</item>
    /// <item><b>Yearly:</b> a twelfth, in every such month, so a year's bill doesn't make one month look like a loss.</item>
    /// </list>
    /// </summary>
    public static decimal InMonth(CostItem cost, DateOnly month)
    {
        var last = month.AddMonths(1).AddDays(-1);
        if (cost.Recurrence == CostRecurrence.Once)
        {
            return cost.StartsOn >= month && cost.StartsOn <= last ? cost.Amount : 0;
        }
        var runs = cost.StartsOn <= last && (cost.EndsOn is null || cost.EndsOn >= month);
        return !runs ? 0 : cost.Recurrence == CostRecurrence.Yearly ? Math.Round(cost.Amount / 12, 2) : cost.Amount;
    }

    /// <summary>
    /// The estimate of what the provider kept: a share of the amount and a fixed fee for each payment.
    /// </summary>
    public static decimal Fee(decimal amount, int payments, FinancialOptions fees) =>
        Math.Round(amount * fees.PaymentFeePercent / 100 + payments * fees.PaymentFeeFixed, 2);

    /// <summary>
    /// What's wrong with a cost as entered, or null.
    /// </summary>
    public static string? Problem(CostItemRequest c) =>
        string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 120 ? "A cost needs a name of at most 120 characters."
        : c.Name.Any(char.IsControl) || c.Notes is { } notes && notes.Any(ch => char.IsControl(ch) && ch != '\n') ? "Control characters aren't allowed."
        : !Categories.Contains(c.Category?.Trim().ToLowerInvariant() ?? "") ? $"Choose a category: {string.Join(", ", Categories)}."
        : c.Amount is <= 0 or > 9_999_999_999.99m ? "The amount is more than zero."
        : decimal.Round(c.Amount, 2) != c.Amount ? "The amount has at most two decimals."
        : c.Currency is null || !CurrencyCode().IsMatch(c.Currency.Trim().ToUpperInvariant()) ? "The currency is three letters, like USD."
        : Recurrence(c.Recurrence) is null ? "It recurs monthly, yearly or once."
        : c.StartsOn.Year is < 2000 or > 2100 ? "The start date isn't a real one."
        : c.EndsOn is { } end && end < c.StartsOn ? "It can't end before it starts."
        : c.Notes is { Length: > 500 } ? "Notes are at most 500 characters."
        : null;

    /// <summary>
    /// Writes a valid request onto the item: trimmed, the currency and category in their stored case, and no end date
    /// on a one-off.
    /// </summary>
    public static void Apply(CostItemRequest c, CostItem item, DateTime nowUtc)
    {
        item.Name = c.Name!.Trim();
        item.Category = c.Category!.Trim().ToLowerInvariant();
        item.Amount = c.Amount;
        item.Currency = c.Currency!.Trim().ToUpperInvariant();
        item.Recurrence = Recurrence(c.Recurrence)!.Value;
        item.StartsOn = c.StartsOn;
        item.EndsOn = item.Recurrence == CostRecurrence.Once ? null : c.EndsOn;
        item.Notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim();
        item.UpdatedAtUtc = nowUtc;
    }

    private static CostRecurrence? Recurrence(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "monthly" => CostRecurrence.Monthly,
        "yearly" => CostRecurrence.Yearly,
        "once" => CostRecurrence.Once,
        _ => null
    };

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}

using System.Text.RegularExpressions;
using Financials.API.Data;

namespace Financials.API.Services;

public record FinancialCategoryDto(string Category, decimal Amount);

/// <summary>
/// One month of the books in one currency: what came in, what went out, and what's left.
/// </summary>
/// <param name="Month">The month's first day.</param>
/// <param name="Fees">An estimate of what the payment provider kept (<see cref="FinancialOptions"/>), not its bill.</param>
/// <param name="Net">Subscriptions and affiliates, less fees and costs.</param>
public record FinancialRowDto(DateOnly Month, decimal Subscriptions, decimal Affiliates, decimal Fees, decimal Costs, decimal Net);

/// <summary>
/// A currency's books. Amounts in different currencies are never added together or converted.
/// </summary>
/// <param name="Months">Newest first; only months with something in them.</param>
/// <param name="Recurring">What the running subscriptions bring in a month, now.</param>
/// <param name="RunningCosts">What the costs running today come to a month, a yearly one counted as a twelfth.</param>
/// <param name="RunRate">What a month leaves at today's rate: subscriptions after their fee share, less running costs.</param>
public record FinancialBookDto(string Currency, IReadOnlyList<FinancialRowDto> Months, FinancialRowDto Total, decimal Recurring,
    decimal RunningCosts, decimal RunRate, IReadOnlyList<FinancialCategoryDto> CostsByCategory);

/// <summary>
/// The business's books for the last months, whole: income from both places it comes from, the fee estimate, the
/// entered costs, and what's left.
/// </summary>
/// <param name="Books">One for each currency, the busiest first.</param>
/// <param name="SubscriptionsKnown">False when Identity.API couldn't be asked: the figures then lack subscriptions.</param>
/// <param name="Affiliates">"on", "off" (affiliate links are switched off) or "unknown" (Catalog.API couldn't be asked).</param>
public record FinancialStatementDto(DateOnly From, DateOnly To, decimal FeePercent, decimal FeeFixed, IReadOnlyList<FinancialBookDto> Books,
    IReadOnlyList<string> Categories, bool SubscriptionsKnown, string Affiliates);

/// <summary>
/// What the payment provider is assumed to keep of each payment, for the fee estimate (<c>Financials</c>). The
/// defaults are Stripe's standard card rate; set them to the rate on the provider's own statement.
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
/// Subscription payments in one month and currency, as Identity.API reports them.
/// </summary>
public sealed record IncomeMonth(DateOnly Month, string Currency, decimal Amount, int Payments);

/// <summary>
/// Affiliate commission in one month and currency, as Catalog.API reports it.
/// </summary>
public sealed record CommissionMonth(DateOnly Month, string Currency, int Conversions, decimal Commission);

public sealed record Money(string Currency, decimal Amount);

/// <summary>
/// The statement's rules, apart from the database and the other services: which months it covers, what a cost comes
/// to in a month, the fee estimate, and how the pieces add up.
/// </summary>
public static partial class FinancialStatement
{
    public const int MaxMonths = 36;

    public const string AffiliatesOn = "on";
    public const string AffiliatesOff = "off";
    public const string AffiliatesUnknown = "unknown";

    /// <summary>
    /// What a cost can be filed under, in the order the page lists them.
    /// </summary>
    public static readonly IReadOnlyList<string> Categories = ["hosting", "data", "services", "payments", "marketing", "legal", "people", "other"];

    public static int Clamp(int months) => Math.Clamp(months, 1, MaxMonths);

    /// <summary>
    /// The first day of the earliest month a statement of <paramref name="months"/> months covers, this month included.
    /// </summary>
    public static DateOnly FirstMonth(DateOnly today, int months) => new DateOnly(today.Year, today.Month, 1).AddMonths(-(Clamp(months) - 1));

    /// <summary>
    /// Null for <paramref name="subscriptions"/> or <paramref name="commission"/> means that service couldn't be asked;
    /// <paramref name="affiliatesOff"/> that affiliate links are switched off, so there is no commission to ask for.
    /// </summary>
    public static FinancialStatementDto Build(DateOnly today, int months, IReadOnlyList<IncomeMonth>? subscriptions, IReadOnlyList<Money> recurring,
        IReadOnlyList<CommissionMonth>? commission, bool affiliatesOff, IReadOnlyList<CostItem> costs, FinancialOptions fees)
    {
        var from = FirstMonth(today, months);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var window = Enumerable.Range(0, Clamp(months)).Select(i => thisMonth.AddMonths(-i)).ToList();

        // Only the statement's own months, whatever the other services sent.
        var paid = (subscriptions ?? []).Where(s => s.Month >= from && s.Month <= thisMonth)
            .GroupBy(s => (s.Month, Currency: s.Currency.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => (Amount: g.Sum(s => s.Amount), Payments: g.Sum(s => s.Payments)));
        var earned = (commission ?? []).Where(c => c.Month >= from && c.Month <= thisMonth)
            .GroupBy(c => (c.Month, Currency: c.Currency.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Commission));
        var spent = window.SelectMany(month => costs.Select(c => (Month: month, c.Currency, c.Category, Amount: InMonth(c, month))))
            .Where(c => c.Amount > 0)
            .ToList();
        var running = costs.Where(c => c.Recurrence != CostRecurrence.Once && c.StartsOn <= today && (c.EndsOn is null || c.EndsOn >= today))
            .GroupBy(c => c.Currency)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(c => c.Recurrence == CostRecurrence.Yearly ? c.Amount / 12 : c.Amount), 2));
        var monthly = recurring.GroupBy(m => m.Currency.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(m => m.Amount));

        var currencies = paid.Keys.Select(k => k.Currency).Concat(earned.Keys.Select(k => k.Currency)).Concat(spent.Select(s => s.Currency))
            .Concat(running.Keys).Concat(monthly.Keys).Distinct(StringComparer.Ordinal);
        var books = currencies.Select(currency =>
            {
                var rows = window
                    .Select(month =>
                    {
                        var (amount, payments) = paid.GetValueOrDefault((month, currency));
                        return Row(month, amount, earned.GetValueOrDefault((month, currency)), Fee(amount, payments, fees),
                            spent.Where(s => s.Month == month && s.Currency == currency).Sum(s => s.Amount));
                    })
                    .Where(r => r.Subscriptions != 0 || r.Affiliates != 0 || r.Costs != 0)
                    .ToList();
                var byCategory = spent.Where(s => s.Currency == currency).GroupBy(s => s.Category)
                    .Select(g => new FinancialCategoryDto(g.Key, g.Sum(s => s.Amount)))
                    .OrderByDescending(c => c.Amount).ThenBy(c => c.Category, StringComparer.Ordinal)
                    .ToList();
                var income = monthly.GetValueOrDefault(currency);
                var outgoing = running.GetValueOrDefault(currency);
                return new FinancialBookDto(currency, rows,
                    Row(from, rows.Sum(r => r.Subscriptions), rows.Sum(r => r.Affiliates), rows.Sum(r => r.Fees), rows.Sum(r => r.Costs)),
                    income, outgoing, Math.Round(income * (1 - fees.PaymentFeePercent / 100) - outgoing, 2), byCategory);
            })
            .OrderByDescending(b => b.Total.Subscriptions + b.Total.Affiliates).ThenByDescending(b => b.Total.Costs)
            .ThenBy(b => b.Currency, StringComparer.Ordinal)
            .ToList();

        return new FinancialStatementDto(from, today, fees.PaymentFeePercent, fees.PaymentFeeFixed, books, Categories, subscriptions is not null,
            affiliatesOff ? AffiliatesOff : commission is null ? AffiliatesUnknown : AffiliatesOn);
    }

    private static FinancialRowDto Row(DateOnly month, decimal subscriptions, decimal affiliates, decimal fees, decimal costs) =>
        new(month, subscriptions, affiliates, fees, costs, subscriptions + affiliates - fees - costs);

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
    public static string? Problem(Apis.CostItemRequest c) =>
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
    public static void Apply(Apis.CostItemRequest c, CostItem item, DateTime nowUtc)
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

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}

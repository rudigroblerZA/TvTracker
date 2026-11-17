using TvTracker.UI.Models;

namespace TvTracker.UI.Services;

/// <summary>
/// One month of the books in one currency: what came in, what went out, and what's left.
/// </summary>
/// <param name="Fees">The estimate of what the payment provider kept.</param>
public record FinancialRow(DateOnly Month, decimal Subscriptions, decimal Affiliates, decimal Fees, decimal Costs)
{
    public decimal Income => Subscriptions + Affiliates;

    public decimal Net => Income - Fees - Costs;
}

/// <summary>
/// A currency's books: its months, newest first, and their totals. Amounts in different currencies are never added
/// together or converted.
/// </summary>
/// <param name="Recurring">What the running subscriptions bring in a month, now.</param>
/// <param name="RunningCosts">What the costs running today come to a month.</param>
public record FinancialBook(string Currency, IReadOnlyList<FinancialRow> Months, FinancialRow Total, decimal Recurring, decimal RunningCosts,
    IReadOnlyList<FinancialCategory> CostsByCategory)
{
    /// <summary>
    /// What a month leaves at today's rate: subscriptions after their estimated fees, less the running costs.
    /// </summary>
    public decimal RunRate(decimal feePercent) => Math.Round(Recurring * (1 - feePercent / 100) - RunningCosts, 2);
}

/// <summary>
/// Puts the two halves of the financials together: Identity.API's statement (subscriptions, fees, costs) and
/// Catalog.API's affiliate commission, by month and currency.
/// </summary>
public static class Financials
{
    public static readonly int[] Windows = [3, 12, 24];

    /// <summary>
    /// One book for each currency anything was earned or spent in, the busiest first (by income, then costs).
    /// </summary>
    public static IReadOnlyList<FinancialBook> Books(FinancialStatement statement, IReadOnlyList<AffiliateMonth> affiliates)
    {
        var currencies = statement.Months.Select(m => m.Currency)
            .Concat(affiliates.Select(a => a.Currency.ToUpperInvariant()))
            .Concat(statement.MonthlyRecurring.Select(m => m.Currency))
            .Concat(statement.MonthlyCosts.Select(m => m.Currency))
            .Distinct(StringComparer.Ordinal);
        return currencies.Select(currency => Book(currency, statement, affiliates))
            .OrderByDescending(b => b.Total.Income).ThenByDescending(b => b.Total.Costs).ThenBy(b => b.Currency, StringComparer.Ordinal)
            .ToList();
    }

    private static FinancialBook Book(string currency, FinancialStatement statement, IReadOnlyList<AffiliateMonth> affiliates)
    {
        var own = statement.Months.Where(m => m.Currency == currency).ToDictionary(m => m.Month);
        // Only the statement's months: the two services clamp their windows the same way, but a commission outside it
        // would otherwise make a month the statement doesn't have.
        var commission = affiliates.Where(a => a.Currency.ToUpperInvariant() == currency && a.Month >= FirstOf(statement.From))
            .GroupBy(a => a.Month).ToDictionary(g => g.Key, g => g.Sum(a => a.Commission));
        var months = own.Keys.Union(commission.Keys).OrderByDescending(m => m)
            .Select(month =>
            {
                var m = own.GetValueOrDefault(month);
                return new FinancialRow(month, m?.Subscriptions ?? 0, commission.GetValueOrDefault(month), m?.PaymentFees ?? 0, m?.Costs ?? 0);
            })
            .ToList();
        var total = new FinancialRow(FirstOf(statement.From), months.Sum(m => m.Subscriptions), months.Sum(m => m.Affiliates), months.Sum(m => m.Fees),
            months.Sum(m => m.Costs));
        var byCategory = own.Values.SelectMany(m => m.CostsByCategory).GroupBy(c => c.Category)
            .Select(g => new FinancialCategory(g.Key, g.Sum(c => c.Amount)))
            .OrderByDescending(c => c.Amount).ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
        return new FinancialBook(currency, months, total, Of(statement.MonthlyRecurring, currency), Of(statement.MonthlyCosts, currency), byCategory);
    }

    private static decimal Of(IEnumerable<Money> money, string currency) => money.Where(m => m.Currency == currency).Sum(m => m.Amount);

    private static DateOnly FirstOf(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>
    /// An amount with its sign said, for a figure that can be a loss: "+12.00 USD", "−3.50 USD".
    /// </summary>
    public static string Signed(decimal amount, string currency) =>
        $"{(amount < 0 ? "−" : amount > 0 ? "+" : "")}{ReportFormat.Amount(Math.Abs(amount), currency)}";

    public static string CategoryName(string category) => category switch
    {
        "hosting" => "Hosting",
        "data" => "Data licences",
        "services" => "Services",
        "payments" => "Payments",
        "marketing" => "Marketing",
        "legal" => "Legal and accounting",
        "people" => "People",
        _ => "Other"
    };

    public static string RecurrenceName(string recurrence) => recurrence switch
    {
        "yearly" => "a year",
        "once" => "once",
        _ => "a month"
    };
}

using TvTracker.UI.Models;
using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// The financials page's sums: the two services' figures put together by month and currency.
/// </summary>
public class FinancialsTests
{
    private static readonly DateOnly Nov = new(2026, 11, 1);
    private static readonly DateOnly Oct = new(2026, 10, 1);

    private static FinancialStatement Statement(IReadOnlyList<FinancialMonth> months, IReadOnlyList<Money>? recurring = null,
        IReadOnlyList<Money>? running = null) =>
        new(new DateOnly(2026, 9, 1), new DateOnly(2026, 11, 17), 2.9m, 0.30m, months, recurring ?? [], running ?? [], ["hosting", "services"]);

    private static FinancialMonth Month(DateOnly month, string currency, decimal subscriptions, decimal fees, decimal costs,
        params FinancialCategory[] categories) =>
        new(month, currency, subscriptions, 1, fees, costs, categories);

    [Fact]
    public void A_month_is_income_from_both_services_less_fees_and_costs()
    {
        var book = Assert.Single(Financials.Books(
            Statement([Month(Nov, "USD", 100m, 3.20m, 40m), Month(Oct, "USD", 50m, 1.75m, 40m)]),
            [new AffiliateMonth(Nov, "usd", 2, 12.50m), new AffiliateMonth(new DateOnly(2026, 9, 1), "USD", 1, 5m)]));

        Assert.Equal(
        [
            new FinancialRow(Nov, 100m, 12.50m, 3.20m, 40m),
            new FinancialRow(Oct, 50m, 0m, 1.75m, 40m),
            // A month with commission only still shows.
            new FinancialRow(new DateOnly(2026, 9, 1), 0m, 5m, 0m, 0m)
        ], book.Months);
        Assert.Equal([69.30m, 8.25m, 5m], book.Months.Select(m => m.Net));
        Assert.Equal((167.50m, 82.55m), (book.Total.Income, book.Total.Net));
    }

    [Fact]
    public void Currencies_are_kept_apart_busiest_first()
    {
        var books = Financials.Books(
            Statement([Month(Nov, "USD", 10m, 0.59m, 0m), Month(Nov, "ZAR", 500m, 14.80m, 100m)], running: [new Money("EUR", 6m)]),
            []);

        Assert.Equal(["ZAR", "USD", "EUR"], books.Select(b => b.Currency));
        // A currency with only a running cost has a book, with no months yet.
        Assert.Empty(books[2].Months);
        Assert.Equal(6m, books[2].RunningCosts);
    }

    [Fact]
    public void Commission_from_before_the_statements_first_month_is_left_out()
    {
        var book = Assert.Single(Financials.Books(Statement([Month(Nov, "USD", 10m, 0.59m, 0m)]), [new AffiliateMonth(new DateOnly(2026, 8, 1), "USD", 1, 99m)]));

        Assert.Equal(0m, book.Total.Affiliates);
    }

    [Fact]
    public void Costs_by_category_add_up_across_the_window_largest_first()
    {
        var book = Assert.Single(Financials.Books(Statement(
        [
            Month(Nov, "USD", 0m, 0m, 30m, new FinancialCategory("hosting", 20m), new FinancialCategory("services", 10m)),
            Month(Oct, "USD", 0m, 0m, 45m, new FinancialCategory("hosting", 20m), new FinancialCategory("services", 25m))
        ]), []));

        Assert.Equal([new FinancialCategory("hosting", 40m), new FinancialCategory("services", 35m)], book.CostsByCategory);
    }

    [Theory]
    [InlineData(100, 40, 57.10)]
    [InlineData(10, 40, -30.29)]
    public void Todays_rate_is_subscriptions_after_their_fee_share_less_running_costs(decimal recurring, decimal costs, decimal expected)
    {
        var book = Assert.Single(Financials.Books(Statement([], [new Money("USD", recurring)], [new Money("USD", costs)]), []));

        Assert.Equal(expected, book.RunRate(2.9m));
    }

    [Theory]
    [InlineData(12, "+12.00 USD")]
    [InlineData(-3.5, "−3.50 USD")]
    [InlineData(0, "0.00 USD")]
    public void A_figure_that_can_be_a_loss_says_its_sign(decimal amount, string expected) =>
        Assert.Equal(expected, Financials.Signed(amount, "USD"));
}

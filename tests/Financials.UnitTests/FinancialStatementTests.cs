using Financials.API.Apis;
using Financials.API.Services;
using Financials.Data;

namespace Financials.UnitTests;

/// <summary>
/// The admins' financial statement: which months it covers, what a cost comes to in a month, the fee estimate, how
/// the two kinds of income and the costs add up by currency, and what a cost may be.
/// </summary>
public class FinancialStatementTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly DateOnly Nov = new(2026, 11, 1);
    private static readonly DateOnly Oct = new(2026, 10, 1);
    private static readonly DateOnly Sep = new(2026, 9, 1);
    private static readonly FinancialOptions Fees = new();

    private static CostItem Cost(decimal amount, CostRecurrence recurrence, DateOnly starts, DateOnly? ends = null, string currency = "USD",
        string category = "hosting") =>
        new() { Id = Guid.NewGuid(), Name = "x", Category = category, Amount = amount, Currency = currency, Recurrence = recurrence, StartsOn = starts, EndsOn = ends };

    private static FinancialStatementDto Build(int months = 3, IReadOnlyList<IncomeMonth>? subscriptions = null, IReadOnlyList<Money>? recurring = null,
        IReadOnlyList<CommissionMonth>? commission = null, bool affiliatesOff = false, params CostItem[] costs) =>
        FinancialStatement.Build(Today, months, new(subscriptions ?? [], recurring ?? []), affiliatesOff ? AffiliateIncome.Off : AffiliateIncome.On(commission ?? []), costs, Fees);

    [Fact]
    public void A_month_whose_payments_come_to_nothing_keeps_its_row_and_its_fees()
    {
        var statement = Build(subscriptions: [new IncomeMonth(Oct, "USD", 0m, 2)]);

        var row = Assert.Single(Assert.Single(statement.Books).Months);
        Assert.Equal(Oct, row.Month);
        Assert.Equal(0m, row.Subscriptions);
        Assert.Equal(FinancialStatement.Fee(0m, 2, Fees), row.Fees);
    }

    // --- The window and a cost's month ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "2026-11-01")]
    [InlineData(12, "2025-12-01")]
    [InlineData(0, "2026-11-01")]
    [InlineData(500, "2023-12-01")]
    public void The_window_is_whole_months_ending_with_this_one(int months, string first) =>
        Assert.Equal(DateOnly.Parse(first), FinancialStatement.FirstMonth(Today, months));

    [Fact]
    public void A_monthly_cost_counts_in_every_month_it_runs_for_any_part_of()
    {
        var cost = Cost(40m, CostRecurrence.Monthly, new DateOnly(2026, 9, 20), new DateOnly(2026, 11, 2));

        Assert.Equal([0m, 40m, 40m, 40m, 0m], new[] { 8, 9, 10, 11, 12 }.Select(m => FinancialStatement.InMonth(cost, new DateOnly(2026, m, 1))));
    }

    [Fact]
    public void A_yearly_cost_is_a_twelfth_each_month_and_a_one_off_falls_in_its_own_month()
    {
        var domain = Cost(120m, CostRecurrence.Yearly, new DateOnly(2026, 1, 15));
        var lawyer = Cost(900m, CostRecurrence.Once, new DateOnly(2026, 10, 31));

        Assert.Equal(10m, FinancialStatement.InMonth(domain, Nov));
        Assert.Equal(0m, FinancialStatement.InMonth(domain, new DateOnly(2025, 12, 1)));
        Assert.Equal([0m, 900m, 0m], new[] { 9, 10, 11 }.Select(m => FinancialStatement.InMonth(lawyer, new DateOnly(2026, m, 1))));
    }

    [Fact]
    public void The_fee_estimate_is_a_share_of_the_amount_and_a_fixed_part_for_each_payment() =>
        // 2.9% of 100 is 2.90, and three payments at 0.30 are 0.90.
        Assert.Equal(3.80m, FinancialStatement.Fee(100m, 3, Fees));

    // --- Adding it up --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_month_is_income_from_both_services_less_fees_and_costs()
    {
        var book = Assert.Single(Build(
            subscriptions: [new(Nov, "usd", 100m, 3), new(Oct, "USD", 50m, 1)],
            commission: [new(Nov, "USD", 12.50m), new(Sep, "usd", 5m)],
            costs: [Cost(40m, CostRecurrence.Monthly, new DateOnly(2026, 10, 1))]).Books);

        Assert.Equal(
        [
            // 2.9% of 100 and three payments at 0.30: 3.80. 100 + 12.50 - 3.80 - 40.
            new FinancialRowDto(Nov, 100m, 12.50m, 3.80m, 40m, 68.70m),
            new FinancialRowDto(Oct, 50m, 0m, 1.75m, 40m, 8.25m),
            // A month with commission only still shows.
            new FinancialRowDto(Sep, 0m, 5m, 0m, 0m, 5m)
        ], book.Months);
        Assert.Equal(new FinancialRowDto(Sep, 150m, 17.50m, 5.55m, 80m, 81.95m), book.Total);
    }

    [Fact]
    public void Currencies_are_kept_apart_busiest_first()
    {
        var statement = Build(
            subscriptions: [new(Nov, "USD", 10m, 1), new(Nov, "ZAR", 500m, 2)],
            costs: [Cost(100m, CostRecurrence.Monthly, Nov, currency: "ZAR"), Cost(6m, CostRecurrence.Monthly, new DateOnly(2026, 12, 1), currency: "EUR"),
                Cost(72m, CostRecurrence.Yearly, new DateOnly(2026, 1, 1), currency: "GBP")]);

        Assert.Equal(["ZAR", "USD", "GBP"], statement.Books.Select(b => b.Currency));
        // The euro cost hasn't started: nothing in the window, and not running today.
        Assert.Equal(6m, statement.Books[2].RunningCosts);
        Assert.Equal(3, statement.Books[2].Months.Count);
    }

    [Fact]
    public void Figures_from_outside_the_window_are_left_out()
    {
        var book = Assert.Single(Build(months: 2,
            subscriptions: [new(Nov, "USD", 10m, 1), new(Sep, "USD", 99m, 1), new(new DateOnly(2026, 12, 1), "USD", 99m, 1)],
            commission: [new(new DateOnly(2026, 8, 1), "USD", 99m)]).Books);

        Assert.Equal((10m, 0m), (book.Total.Subscriptions, book.Total.Affiliates));
    }

    [Fact]
    public void Costs_by_category_add_up_across_the_window_largest_first()
    {
        var book = Assert.Single(Build(months: 2, costs:
        [
            Cost(20m, CostRecurrence.Monthly, new DateOnly(2026, 1, 1)),
            Cost(300m, CostRecurrence.Yearly, new DateOnly(2026, 1, 1), category: "services"),
            Cost(5m, CostRecurrence.Once, new DateOnly(2026, 10, 9), category: "legal")
        ]).Books);

        Assert.Equal([new FinancialCategoryDto("services", 50m), new FinancialCategoryDto("hosting", 40m), new FinancialCategoryDto("legal", 5m)],
            book.CostsByCategory);
    }

    [Theory]
    [InlineData(100, 57.10)]
    [InlineData(10, -30.29)]
    public void Todays_rate_is_subscriptions_after_their_fee_share_less_the_costs_running_today(decimal recurring, decimal expected)
    {
        var book = Assert.Single(Build(months: 1, recurring: [new("usd", recurring)], costs:
        [
            Cost(30m, CostRecurrence.Monthly, new DateOnly(2026, 1, 1)),
            Cost(120m, CostRecurrence.Yearly, new DateOnly(2026, 1, 1)),
            // Not running today: ended, still to start, and a one-off.
            Cost(15m, CostRecurrence.Monthly, new DateOnly(2026, 1, 1), ends: new DateOnly(2026, 10, 31)),
            Cost(15m, CostRecurrence.Monthly, new DateOnly(2026, 12, 1)),
            Cost(900m, CostRecurrence.Once, new DateOnly(2026, 11, 5), currency: "ZAR")
        ]).Books, b => b.Currency == "USD");

        Assert.Equal((recurring, 40m, expected), (book.Recurring, book.RunningCosts, book.RunRate));
    }

    [Fact]
    public void A_statement_says_which_income_it_couldnt_get()
    {
        var whole = Build();
        var noIdentity = FinancialStatement.Build(Today, 3, subscriptions: null, AffiliateIncome.On([]), costs: [], Fees);
        var noCatalog = FinancialStatement.Build(Today, 3, new([], []), AffiliateIncome.Unknown, costs: [], Fees);
        var off = FinancialStatement.Build(Today, 3, new([], []), AffiliateIncome.Off, costs: [], Fees);

        Assert.Equal((true, "on"), (whole.SubscriptionsKnown, whole.Affiliates));
        Assert.False(noIdentity.SubscriptionsKnown);
        Assert.Equal("unknown", noCatalog.Affiliates);
        Assert.Equal("off", off.Affiliates);
    }

    // --- What a cost may be ----------------------------------------------------------------------------------------------

    private static CostItemRequest Request(string? name = "Server", string? category = "hosting", decimal amount = 20m, string? currency = "usd",
        string? recurrence = "Monthly", string starts = "2026-11-01", string? ends = null, string? notes = null) =>
        new(name, category, amount, currency, recurrence, DateOnly.Parse(starts), ends is null ? null : DateOnly.Parse(ends), notes);

    [Fact]
    public void A_cost_as_entered_is_stored_trimmed_with_its_currency_in_capitals()
    {
        var item = new CostItem { Name = "", Category = "", Currency = "" };
        var request = Request(name: "  Server ", category: " Hosting ", currency: " usd ", notes: "  ");

        Assert.Null(FinancialStatement.Problem(request));
        FinancialStatement.Apply(request, item, new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(("Server", "hosting", "USD", CostRecurrence.Monthly, (string?)null), (item.Name, item.Category, item.Currency, item.Recurrence, item.Notes));
    }

    [Fact]
    public void A_one_off_has_no_end_date()
    {
        var item = new CostItem { Name = "", Category = "", Currency = "" };

        FinancialStatement.Apply(Request(recurrence: "once", ends: "2026-12-31"), item, DateTime.UtcNow);

        Assert.Null(item.EndsOn);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("category")]
    [InlineData("zero")]
    [InlineData("thirds")]
    [InlineData("currency")]
    [InlineData("recurrence")]
    [InlineData("ends")]
    [InlineData("notes")]
    public void A_cost_that_makes_no_sense_is_refused(string what)
    {
        var request = what switch
        {
            "name" => Request(name: " "),
            "category" => Request(category: "yachts"),
            "zero" => Request(amount: 0m),
            "thirds" => Request(amount: 1.005m),
            "currency" => Request(currency: "dollars"),
            "recurrence" => Request(recurrence: "weekly"),
            "ends" => Request(ends: "2026-10-31"),
            _ => Request(notes: new string('n', 501))
        };

        Assert.NotNull(FinancialStatement.Problem(request));
    }

    [Theory]
    [InlineData(2.9, 0.30, null)]
    [InlineData(0, 0, null)]
    [InlineData(-1, 0.30, "between 0 and 30")]
    [InlineData(31, 0.30, "between 0 and 30")]
    [InlineData(1.5, 30, null)]
    [InlineData(2.9, 10001, "between 0 and 10000")]
    [InlineData(2.955, 0.30, "two decimals")]
    public void A_fee_rate_is_a_sensible_percentage_and_a_small_fixed_part(double percent, double @fixed, string? problem)
    {
        var found = FinancialStatement.Problem(new Financials.API.Apis.FeeSettingsRequest((decimal)percent, (decimal)@fixed));

        if (problem is null) { Assert.Null(found); } else { Assert.Contains(problem, found); }
    }

    [Fact]
    public void A_fee_rate_with_a_part_left_out_isnt_taken_for_zero()
    {
        Assert.Contains("both", FinancialStatement.Problem(new Financials.API.Apis.FeeSettingsRequest(2.9m, null)));
        Assert.Contains("both", FinancialStatement.Problem(new Financials.API.Apis.FeeSettingsRequest(null, null)));
    }
}

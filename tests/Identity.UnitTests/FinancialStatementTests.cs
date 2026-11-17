using Identity.API.Apis;
using Identity.API.Data;

namespace Identity.UnitTests;

/// <summary>
/// The admins' financial statement: which months it covers, what a cost comes to in a month, the fee estimate, and
/// what a cost may be.
/// </summary>
public class FinancialStatementTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly FinancialOptions Fees = new();

    private static DateTime At(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    private static CostItem Cost(decimal amount, CostRecurrence recurrence, DateOnly starts, DateOnly? ends = null, string currency = "USD",
        string category = "hosting") =>
        new() { Id = Guid.NewGuid(), Name = "x", Category = category, Amount = amount, Currency = currency, Recurrence = recurrence, StartsOn = starts, EndsOn = ends };

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

        Assert.Equal(10m, FinancialStatement.InMonth(domain, new DateOnly(2026, 11, 1)));
        Assert.Equal(0m, FinancialStatement.InMonth(domain, new DateOnly(2025, 12, 1)));
        Assert.Equal([0m, 900m, 0m], new[] { 9, 10, 11 }.Select(m => FinancialStatement.InMonth(lawyer, new DateOnly(2026, m, 1))));
    }

    [Fact]
    public void The_fee_estimate_is_a_share_of_the_amount_and_a_fixed_part_for_each_payment() =>
        // 2.9% of 100 is 2.90, and three payments at 0.30 are 0.90.
        Assert.Equal(3.80m, FinancialStatement.Fee(100m, 3, Fees));

    [Fact]
    public void Months_are_newest_first_by_currency_with_only_what_has_something_in_it()
    {
        var statement = FinancialStatement.Build(Today, months: 3,
            receipts: [new(At(11, 3), 2.99m, "usd"), new(At(11, 9), 29.99m, "USD"), new(At(10, 30), 89m, "ZAR")],
            costs:
            [
                Cost(20m, CostRecurrence.Monthly, new DateOnly(2026, 10, 1)),
                Cost(120m, CostRecurrence.Yearly, new DateOnly(2026, 1, 1), category: "services"),
                Cost(500m, CostRecurrence.Once, new DateOnly(2026, 9, 12), category: "legal", currency: "ZAR")
            ],
            active: [], Fees);

        Assert.Equal((new DateOnly(2026, 9, 1), Today), (statement.From, statement.To));
        Assert.Equal(
        [
            (new DateOnly(2026, 11, 1), "USD", 32.98m, 2, 1.56m, 30m),
            (new DateOnly(2026, 10, 1), "USD", 0m, 0, 0m, 30m),
            (new DateOnly(2026, 10, 1), "ZAR", 89m, 1, 2.88m, 0m),
            (new DateOnly(2026, 9, 1), "USD", 0m, 0, 0m, 10m),
            (new DateOnly(2026, 9, 1), "ZAR", 0m, 0, 0m, 500m)
        ], statement.Months.Select(m => (m.Month, m.Currency, m.Subscriptions, m.Payments, m.PaymentFees, m.Costs)));
        // By category, in the page's order: hosting before services.
        Assert.Equal([new FinancialCategoryDto("hosting", 20m), new FinancialCategoryDto("services", 10m)], statement.Months[0].CostsByCategory);
    }

    [Fact]
    public void Todays_rate_is_running_subscriptions_and_running_costs_a_month()
    {
        var statement = FinancialStatement.Build(Today, months: 1, receipts: [],
            costs:
            [
                Cost(20m, CostRecurrence.Monthly, new DateOnly(2026, 1, 1)),
                Cost(120m, CostRecurrence.Yearly, new DateOnly(2026, 1, 1)),
                Cost(15m, CostRecurrence.Monthly, new DateOnly(2026, 1, 1), ends: new DateOnly(2026, 10, 31)),
                Cost(15m, CostRecurrence.Monthly, new DateOnly(2026, 12, 1)),
                Cost(900m, CostRecurrence.Once, new DateOnly(2026, 11, 5))
            ],
            active: [new("a", "plus", "month", 2.99m, "USD", false), new("b", "plus", "year", 29.99m, "USD", true)], Fees);

        Assert.Equal([new RevenueMoneyDto("USD", 5.49m)], statement.MonthlyRecurring);
        // The ended one, the one still to start and the one-off aren't running today.
        Assert.Equal([new RevenueMoneyDto("USD", 30m)], statement.MonthlyCosts);
    }

    private static CostItemRequest Request(string? name = "Server", string? category = "hosting", decimal amount = 20m, string? currency = "usd",
        string? recurrence = "Monthly", string starts = "2026-11-01", string? ends = null, string? notes = null) =>
        new(name, category, amount, currency, recurrence, DateOnly.Parse(starts), ends is null ? null : DateOnly.Parse(ends), notes);

    [Fact]
    public void A_cost_as_entered_is_stored_trimmed_with_its_currency_in_capitals()
    {
        var item = new CostItem { Name = "", Category = "", Currency = "" };
        var request = Request(name: "  Server ", category: " Hosting ", currency: " usd ", notes: "  ");

        Assert.Null(FinancialStatement.Problem(request));
        FinancialStatement.Apply(request, item, At(11, 17));

        Assert.Equal(("Server", "hosting", "USD", CostRecurrence.Monthly, (string?)null), (item.Name, item.Category, item.Currency, item.Recurrence, item.Notes));
    }

    [Fact]
    public void A_one_off_has_no_end_date()
    {
        var item = new CostItem { Name = "", Category = "", Currency = "" };

        FinancialStatement.Apply(Request(recurrence: "once", ends: "2026-12-31"), item, At(11, 17));

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
}

using Identity.API.Apis;

namespace Identity.UnitTests;

/// <summary>
/// The admins' revenue report: who pays, the free-to-paid rate, and what subscriptions bring in.
/// </summary>
public class RevenueReportTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 3);

    private static DateTime At(int day, int hour = 12) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Monthly_recurring_counts_a_yearly_plan_as_a_twelfth_per_currency()
    {
        var report = RevenueReport.Build(From, To, accounts: 10, onPaidPlan: 4, newAccounts: [], active:
        [
            new("a", "plus", "month", 2.99m, "USD", false),
            new("b", "plus", "year", 29.99m, "usd", false),
            new("c", "family", "month", 89m, "ZAR", true)
        ], started: [], ended: 0, receipts: [], checkoutsStarted: 0, checkoutsCompleted: 0);

        Assert.Equal([new RevenueMoneyDto("USD", 5.49m), new RevenueMoneyDto("ZAR", 89m)], report.MonthlyRecurring);
        Assert.Equal(3, report.PayingAccounts);
        Assert.Equal(0.3, report.PaidShare);
        // A family's members are on a paid plan without paying.
        Assert.Equal(4, report.OnPaidPlan);
    }

    [Fact]
    public void Plans_list_who_pays_by_plan_and_interval_with_those_set_to_end()
    {
        var report = RevenueReport.Build(From, To, 5, 3, [], active:
        [
            new("a", "family", "year", 49.99m, "USD", false),
            new("b", "plus", "month", 2.99m, "USD", true),
            new("c", "plus", "month", 2.99m, "USD", false)
        ], [], 0, [], 0, 0);

        Assert.Equal(
            [new RevenuePlanRowDto("plus", "Pro", "month", 2, 1), new RevenuePlanRowDto("family", "Family", "year", 1, 0)],
            report.Plans);
    }

    [Fact]
    public void Days_and_totals_cover_the_window()
    {
        var report = RevenueReport.Build(From, To, 20, 2, newAccounts: [At(1), At(1, 23), At(3)], active: [],
            started: [At(2)], ended: 1,
            receipts: [new(At(2), 2.99m, "USD"), new(At(3), 29.99m, "USD")], checkoutsStarted: 4, checkoutsCompleted: 2);

        Assert.Equal(3, report.NewAccounts);
        Assert.Equal([new RevenueMoneyDto("USD", 32.98m)], report.Received);
        Assert.Equal((4, 2, 1, 1), (report.CheckoutsStarted, report.CheckoutsCompleted, report.NewSubscriptions, report.Ended));
        Assert.Equal(
            [new RevenueDayDto(From, 2, 0, 0), new RevenueDayDto(From.AddDays(1), 0, 1, 1), new RevenueDayDto(To, 1, 1, 0)],
            report.Days);
    }

    [Fact]
    public void With_no_accounts_the_paid_share_is_zero()
    {
        var report = RevenueReport.Build(From, To, 0, 0, [], [], [], 0, [], 0, 0);

        Assert.Equal(0, report.PaidShare);
        Assert.Empty(report.MonthlyRecurring);
        Assert.Equal(3, report.Days.Count);
    }
}

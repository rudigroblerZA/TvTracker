using Analytics.Worker;

namespace Analytics.UnitTests;

/// <summary>
/// The insights-run alert (CONTEXT.md, Alerts): a failed last run fires at once, and a run that never finishes fires once
/// two intervals and the start's delay have gone by.
/// </summary>
public class InsightsRunAlertTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Overdue = TimeSpan.FromMinutes(121);

    [Fact]
    public void A_failed_run_fires_until_a_later_one_succeeds()
    {
        Assert.True(InsightsRunAlert.Evaluate(Start, null, Start.AddMinutes(2), "timeout", Overdue, Start.AddMinutes(3)).Firing);
        Assert.True(InsightsRunAlert.Evaluate(Start, Start.AddMinutes(2), Start.AddMinutes(62), "timeout", Overdue, Start.AddMinutes(63)).Firing);
        Assert.False(InsightsRunAlert.Evaluate(Start, Start.AddMinutes(122), Start.AddMinutes(62), "timeout", Overdue, Start.AddMinutes(123)).Firing);
    }

    [Fact]
    public void A_run_that_never_finishes_fires_after_two_intervals_and_the_delay()
    {
        Assert.False(InsightsRunAlert.Evaluate(Start, null, null, null, Overdue, Start.AddMinutes(120)).Firing);
        Assert.True(InsightsRunAlert.Evaluate(Start, null, null, null, Overdue, Start.AddMinutes(122)).Firing);
        Assert.True(InsightsRunAlert.Evaluate(Start, Start.AddMinutes(2), null, null, Overdue, Start.AddMinutes(124)).Firing);
    }
}

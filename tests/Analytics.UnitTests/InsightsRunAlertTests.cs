using Analytics.Worker;

namespace Analytics.UnitTests;

/// <summary>
/// The insights-run alert: a failed last run fires at once, and a run that never finishes fires once
/// two intervals and the start's delay have gone by.
/// </summary>
public class InsightsRunAlertTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Overdue = TimeSpan.FromMinutes(121);

    private static bool Fires(DateTimeOffset? lastSuccess, DateTimeOffset? failedAt, int minutesAfterStart) =>
        InsightsRunAlert.Evaluate(new InsightsRunStatus.Snapshot(Start, lastSuccess, failedAt, failedAt is null ? null : "timeout",
            Overdue, Start.AddMinutes(minutesAfterStart))).Firing;

    [Fact]
    public void A_failed_run_fires() => Assert.True(Fires(Start.AddMinutes(2), Start.AddMinutes(62), 63));

    [Fact]
    public void A_run_that_never_finishes_fires_after_two_intervals_and_the_delay()
    {
        Assert.False(Fires(null, null, 120));
        Assert.True(Fires(null, null, 122));
        Assert.False(Fires(Start.AddMinutes(2), null, 122));
        Assert.True(Fires(Start.AddMinutes(2), null, 124));
    }

    [Fact]
    public void A_success_clears_the_failure()
    {
        var time = new FixedTime(Start);
        var status = new InsightsRunStatus(time);
        status.Failed(new TimeoutException("timeout"));
        Assert.True(InsightsRunAlert.Evaluate(status.Now()).Firing);
        status.Succeeded();
        Assert.False(InsightsRunAlert.Evaluate(status.Now()).Firing);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

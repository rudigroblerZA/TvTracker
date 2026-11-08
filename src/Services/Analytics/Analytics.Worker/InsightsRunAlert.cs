using TvTracker;

namespace Analytics.Worker;

/// <summary>
/// What the insights runs have done so far, and how long a run may take, kept by <see cref="InsightsRefresher"/> for
/// <see cref="InsightsRunAlert"/>.
/// </summary>
public sealed class InsightsRunStatus(TimeProvider time)
{
    private readonly DateTimeOffset startedAt = time.GetUtcNow();
    private TimeSpan overdueAfter = TimeSpan.FromHours(2);
    private DateTimeOffset? lastSuccess;
    private (DateTimeOffset At, string Error)? failure;

    /// <summary>
    /// Called by the refresher as it starts: a run is overdue after two of its intervals plus the delay before the first.
    /// </summary>
    public void Schedule(TimeSpan firstRunDelay, TimeSpan interval) => overdueAfter = firstRunDelay + interval * 2;

    public void Succeeded()
    {
        lastSuccess = time.GetUtcNow();
        failure = null;
    }

    public void Failed(Exception ex) => failure = (time.GetUtcNow(), ex.Message);

    public Snapshot Now() => new(startedAt, lastSuccess, failure?.At, failure?.Error, overdueAfter, time.GetUtcNow());

    /// <param name="FailedAt">When the latest run failed; null once a later one succeeds.</param>
    public sealed record Snapshot(DateTimeOffset StartedAt, DateTimeOffset? LastSuccess, DateTimeOffset? FailedAt, string? Error,
        TimeSpan OverdueAfter, DateTimeOffset At);
}

/// <summary>
/// Fires when the latest insights run failed ("Computing insights failed"), or when none has finished within two
/// intervals (and the start's delay) of the worker starting or of the last one: a run that hangs logs nothing. Every
/// show page's insights, the discovery lists, the tags and DNA sent to the catalog and Recommendations, and the season
/// verdicts stop moving while it fires. It resolves with the next run that finishes.
/// </summary>
public sealed class InsightsRunAlert(InsightsRunStatus status) : IAlertCheck
{
    public string Name => "insights-run";

    public Task<AlertState> CheckAsync(CancellationToken ct) => Task.FromResult(Evaluate(status.Now()));

    internal static AlertState Evaluate(InsightsRunStatus.Snapshot s)
    {
        if (s.FailedAt is { } failed)
        {
            return new AlertState(true, $"The last run failed at {failed:u}: {s.Error}");
        }
        var since = s.LastSuccess ?? s.StartedAt;
        if (s.At - since > s.OverdueAfter)
        {
            return new AlertState(true, s.LastSuccess is null
                ? $"No run has finished since the worker started at {s.StartedAt:u}"
                : $"No run has finished since {since:u}");
        }
        return new AlertState(false, s.LastSuccess is { } last ? $"The last run finished at {last:u}" : "The first run hasn't finished yet");
    }
}

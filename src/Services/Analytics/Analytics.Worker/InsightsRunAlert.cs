using TvTracker;

namespace Analytics.Worker;

/// <summary>
/// What the last insights run did, kept by <see cref="InsightsRefresher"/> for <see cref="InsightsRunAlert"/>.
/// </summary>
public sealed class InsightsRunStatus(TimeProvider time)
{
    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();
    public DateTimeOffset? LastSuccess { get; private set; }
    public DateTimeOffset? LastFailure { get; private set; }
    public string? LastError { get; private set; }

    public void Succeeded() => LastSuccess = time.GetUtcNow();

    public void Failed(Exception ex)
    {
        LastFailure = time.GetUtcNow();
        LastError = ex.Message;
    }
}

/// <summary>
/// Fires when the latest insights run failed ("Computing insights failed"), or when none has finished within two
/// intervals (and the start's delay) of the worker starting or of the last one: a run that hangs logs nothing. Every
/// show page's insights, the discovery lists, the tags and DNA sent to the catalog and Recommendations, and the season
/// verdicts stop moving while it fires. It resolves with the next run that finishes.
/// </summary>
public sealed class InsightsRunAlert(InsightsRunStatus status, IConfiguration configuration, TimeProvider time) : IAlertCheck
{
    public string Name => "insights-run";

    public Task<AlertState> CheckAsync(IServiceProvider services, CancellationToken ct) =>
        Task.FromResult(Evaluate(status.StartedAt, status.LastSuccess, status.LastFailure, status.LastError,
            InsightsRefresher.Interval(configuration) * 2 + InsightsRefresher.FirstRunDelay(configuration), time.GetUtcNow()));

    internal static AlertState Evaluate(DateTimeOffset startedAt, DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure,
        string? error, TimeSpan overdueAfter, DateTimeOffset now)
    {
        if (lastFailure is { } failed && (lastSuccess is not { } ok || failed > ok))
        {
            return new AlertState(true, $"The last run failed at {failed:u}: {error}");
        }
        var since = lastSuccess ?? startedAt;
        if (now - since > overdueAfter)
        {
            return new AlertState(true, lastSuccess is null
                ? $"No run has finished since the worker started at {startedAt:u}"
                : $"No run has finished since {since:u}");
        }
        return new AlertState(false, lastSuccess is { } last ? $"The last run finished at {last:u}" : "The first run hasn't finished yet");
    }
}

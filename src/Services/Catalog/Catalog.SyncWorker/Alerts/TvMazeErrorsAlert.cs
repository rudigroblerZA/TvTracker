using Catalog.SyncWorker.Providers;
using TvTracker;

namespace Catalog.SyncWorker.Alerts;

/// <summary>
/// Fires when 10% or more of the last 15 minutes' TVmaze calls failed (a server error, no answer, or 429), with at least
/// 20 calls to judge by. Every loop catches per show and retries later, so an outage shows only as shows going stale and
/// searches importing nothing; and a run of 429s means the shared limiter is wrong (a second sync worker, see the TVmaze
/// budget). Fifteen minutes, not the hour <c>/ops</c> reports, so it resolves soon after TVmaze recovers.
/// </summary>
public sealed class TvMazeErrorsAlert(TvMazeUsage usage) : IAlertCheck
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const int MinCalls = 20;
    public const double MaxErrorShare = 0.1;

    public string Name => "tvmaze-errors";

    public Task<AlertState> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        var (calls, errors) = usage.Errors(Window);
        return Task.FromResult(Evaluate(calls, errors));
    }

    internal static AlertState Evaluate(int calls, int errors)
    {
        var detail = $"{errors} of {calls} TVmaze call{(calls == 1 ? "" : "s")} in the last 15 minutes failed";
        return new AlertState(calls >= MinCalls && errors >= calls * MaxErrorShare, detail);
    }
}

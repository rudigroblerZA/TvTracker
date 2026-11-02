using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Catalog.SyncWorker.Providers;

/// <summary>
/// Who a TVmaze call is for: people waiting on it, or the background jobs that keep the catalog fresh.
/// </summary>
public enum TvMazeLane
{
    Background,
    Interactive
}

/// <summary>
/// The caller of the TVmaze calls made in the current flow (an <see cref="AsyncLocal{T}"/>), set by each entry point
/// with <see cref="Begin"/>: the limiter picks its lane by it and the usage is counted under its name. Calls made outside
/// any scope are background work named "other".
/// </summary>
public static class TvMazeCaller
{
    private static readonly AsyncLocal<(string Name, TvMazeLane Lane)?> current = new();

    public static (string Name, TvMazeLane Lane) Current => current.Value ?? ("other", TvMazeLane.Background);

    public static IDisposable Begin(string name, TvMazeLane lane)
    {
        var previous = current.Value;
        current.Value = (name, lane);
        return new Scope(previous);
    }

    private sealed class Scope((string, TvMazeLane)? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}

/// <summary>
/// TVmaze's budget, shared by every call this process makes: at most <see cref="PermitLimit"/> calls in any
/// <see cref="Window"/> (TVmaze allows at least 20; two are left for retries and clocks), and two lanes.
/// <list type="bullet">
/// <item><b>Interactive first:</b> a waiting interactive call (a search import, a history import's lookup) always goes
/// before any background one, so a backfill never makes a person wait behind its queue.</item>
/// <item><b>Background leaves room:</b> background calls only start while fewer than <see cref="BackgroundLimit"/> calls
/// were made in the window, so a few permits are always free for someone who turns up.</item>
/// </list>
/// A sliding log of grant times rather than .NET's <c>SlidingWindowRateLimiter</c>, which has one first-come queue and no
/// priorities. Permits are never returned: a call counts for the window whether or not it's still running.
/// </summary>
public sealed class TvMazeRateLimiter(TimeProvider time)
{
    public const int PermitLimit = 18;

    /// <summary>
    /// Background calls start only below this many in the window, leaving the rest for interactive ones.
    /// </summary>
    public const int BackgroundLimit = 12;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> granted = new();
    private readonly LinkedList<TaskCompletionSource> interactive = new();
    private readonly LinkedList<TaskCompletionSource> background = new();
    private ITimer? timer;

    /// <summary>
    /// Calls waiting, per lane (for the usage report).
    /// </summary>
    public (int Interactive, int Background) Waiting
    {
        get
        {
            lock (gate)
            {
                return (interactive.Count, background.Count);
            }
        }
    }

    /// <summary>
    /// Waits for a permit in <paramref name="lane"/>; completes at once when one is free.
    /// </summary>
    public Task AcquireAsync(TvMazeLane lane, CancellationToken ct)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            Expire(now);
            var queue = lane == TvMazeLane.Interactive ? interactive : background;
            if (queue.Count == 0 && (lane == TvMazeLane.Interactive || interactive.Count == 0) && granted.Count < Limit(lane))
            {
                granted.Enqueue(now);
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = queue.AddLast(waiter);
            if (ct.CanBeCanceled)
            {
                var registration = ct.Register(() =>
                {
                    lock (gate)
                    {
                        if (node.List is not null)
                        {
                            node.List.Remove(node);
                        }
                    }
                    waiter.TrySetCanceled(ct);
                });
                waiter.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }
            Schedule(now);
            return waiter.Task;
        }
    }

    private static int Limit(TvMazeLane lane) => lane == TvMazeLane.Interactive ? PermitLimit : BackgroundLimit;

    private void Expire(DateTimeOffset now)
    {
        while (granted.Count > 0 && granted.Peek() <= now - Window)
        {
            granted.Dequeue();
        }
    }

    /// <summary>
    /// Grants what the window allows, interactive first, then arms the timer for when the oldest grant leaves it.
    /// </summary>
    private void Pump()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            Expire(now);
            while (interactive.Count > 0 && granted.Count < PermitLimit)
            {
                Grant(interactive, now);
            }
            while (interactive.Count == 0 && background.Count > 0 && granted.Count < BackgroundLimit)
            {
                Grant(background, now);
            }
            Schedule(now);
        }
    }

    private void Grant(LinkedList<TaskCompletionSource> queue, DateTimeOffset now)
    {
        var waiter = queue.First!.Value;
        queue.RemoveFirst();
        if (waiter.TrySetResult())
        {
            granted.Enqueue(now);
        }
    }

    private void Schedule(DateTimeOffset now)
    {
        if (interactive.Count == 0 && background.Count == 0)
        {
            return;
        }
        var due = granted.Count == 0 ? TimeSpan.Zero : granted.Peek() + Window - now;
        timer ??= time.CreateTimer(_ => Pump(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }
}

/// <summary>
/// What TVmaze calls are spent on, by caller: the <c>TvTracker.Catalog.TvMaze</c> meter (calls by caller, lane and
/// status; time waited for a permit) for the dashboard, and the last hour kept in memory for <c>/ops</c>.
/// </summary>
public sealed class TvMazeUsage
{
    public const string MeterName = "TvTracker.Catalog.TvMaze";

    private static readonly TimeSpan Kept = TimeSpan.FromHours(1);

    private readonly Counter<long> calls;
    private readonly Histogram<double> waits;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Queue<Call> recent = new();

    public TvMazeUsage(IMeterFactory meters, TimeProvider time)
    {
        this.time = time;
        var meter = meters.Create(MeterName);
        calls = meter.CreateCounter<long>("tvmaze.calls", description: "TVmaze calls made, by caller, lane and HTTP status (0 when no answer came)");
        waits = meter.CreateHistogram<double>("tvmaze.permit.wait", "ms", "Time a TVmaze call waited for a permit");
    }

    private sealed record Call(DateTimeOffset At, string Caller, TvMazeLane Lane, int Status, double WaitedMs);

    public void Record(string caller, TvMazeLane lane, int status, TimeSpan waited)
    {
        var tags = new TagList { { "caller", caller }, { "lane", lane == TvMazeLane.Interactive ? "interactive" : "background" } };
        waits.Record(waited.TotalMilliseconds, tags);
        tags.Add("status", status);
        calls.Add(1, tags);

        lock (gate)
        {
            var now = time.GetUtcNow();
            recent.Enqueue(new Call(now, caller, lane, status, waited.TotalMilliseconds));
            while (recent.Count > 0 && recent.Peek().At < now - Kept)
            {
                recent.Dequeue();
            }
        }
    }

    /// <summary>
    /// The last hour, per caller, busiest first.
    /// </summary>
    public TvMazeUsageReport Report((int Interactive, int Background) waiting)
    {
        List<Call> calls;
        lock (gate)
        {
            var now = time.GetUtcNow();
            calls = recent.Where(c => c.At >= now - Kept).ToList();
        }
        return new TvMazeUsageReport(calls.Count, calls.Count(c => c.Status == 429), TvMazeRateLimiter.PermitLimit, TvMazeRateLimiter.BackgroundLimit,
            waiting.Interactive, waiting.Background,
            calls.GroupBy(c => (c.Caller, c.Lane))
                .Select(g => new TvMazeCallerUsage(g.Key.Caller, g.Key.Lane == TvMazeLane.Interactive ? "interactive" : "background", g.Count(),
                    g.Count(c => c.Status == 429), g.Count(c => c.Status is 0 or >= 500), Math.Round(g.Average(c => c.WaitedMs)),
                    Math.Round(g.Max(c => c.WaitedMs))))
                .OrderByDescending(c => c.Calls).ThenBy(c => c.Caller, StringComparer.Ordinal)
                .ToList());
    }
}

/// <param name="Calls">TVmaze calls in the last hour; the most it allows is 7,200, and this process stops at 6,480.</param>
public record TvMazeUsageReport(int Calls, int TooMany, int PermitsPerWindow, int BackgroundPermits, int WaitingInteractive,
    int WaitingBackground, IReadOnlyList<TvMazeCallerUsage> Callers);

/// <param name="Failed">Calls answered with a server error, or not at all.</param>
public record TvMazeCallerUsage(string Caller, string Lane, int Calls, int TooMany, int Failed, double AverageWaitMs, double MaxWaitMs);

/// <summary>
/// Every TVmaze call goes through here: it waits for a permit in its caller's lane, then records the call.
/// </summary>
public sealed class TvMazeRateLimitHandler(TvMazeRateLimiter limiter, TvMazeUsage usage, TimeProvider time) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (caller, lane) = TvMazeCaller.Current;
        var started = time.GetTimestamp();
        await limiter.AcquireAsync(lane, cancellationToken);
        var waited = time.GetElapsedTime(started);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            usage.Record(caller, lane, (int)response.StatusCode, waited);
            return response;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            usage.Record(caller, lane, 0, waited);
            throw;
        }
    }
}

namespace TvTracker;

/// <summary>
/// How a background job is doing, as its service reports it to the import CLI (<c>tvtracker-import wikidata</c>).
/// </summary>
/// <param name="Name">The job's name, as the CLI gives it ("atlas", "fill", "research").</param>
/// <param name="State">"off" (it doesn't run in this configuration), "running" or "idle".</param>
/// <param name="Requested">Asked to run now, and not finished with that yet.</param>
/// <param name="Due">How much is waiting for it (shows to look up); null when the job doesn't count that.</param>
/// <param name="Done">How much it has done since it was last asked to run (or since the service started).</param>
/// <param name="LastRunAtUtc">When it last finished a round.</param>
/// <param name="LastError">Why its last round failed; null after one that worked.</param>
/// <param name="Note">Why it's off, or what "due" and "done" count.</param>
public sealed record JobStatus(string Name, string State, bool Requested, int? Due, int Done, DateTime? LastRunAtUtc, string? LastError, string? Note);

/// <summary>
/// A background job that sleeps between rounds, made runnable on demand: its loop sleeps through
/// <see cref="SleepAsync"/>, which ends early when <see cref="Request"/> is called, and reports its rounds here so an
/// endpoint can say how it's doing. A singleton per job, shared by the job and the endpoint.
/// </summary>
public sealed class JobRun(string name, string? note = null)
{
    private readonly Lock sync = new();
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool off;
    private bool running;
    private bool requested;
    private int? due;
    private int done;
    private DateTime? lastRunAtUtc;
    private string? lastError;
    private string? currentNote = note;

    public string Name => name;

    /// <summary>
    /// Asked to run now and not yet done with it: the job works through everything due without its usual pauses.
    /// </summary>
    public bool Requested
    {
        get { lock (sync) { return requested; } }
    }

    public JobStatus Status
    {
        get
        {
            lock (sync)
            {
                return new JobStatus(name, off ? "off" : running ? "running" : "idle", requested, due, done, lastRunAtUtc, lastError, currentNote);
            }
        }
    }

    /// <summary>
    /// Asks the job to run now. False when it's off; asking again while it's at it changes nothing.
    /// </summary>
    public bool Request()
    {
        lock (sync)
        {
            if (off)
            {
                return false;
            }
            if (!requested)
            {
                requested = true;
                done = 0;
                lastError = null;
            }
            wake.TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// The job doesn't run in this configuration, and why.
    /// </summary>
    public void TurnOff(string why)
    {
        lock (sync)
        {
            off = true;
            requested = false;
            currentNote = why;
        }
    }

    /// <summary>
    /// Waits <paramref name="delay"/>, or until the job is asked to run, whichever is first.
    /// </summary>
    public async Task SleepAsync(TimeSpan delay, TimeProvider time, CancellationToken ct)
    {
        Task asked;
        lock (sync)
        {
            if (requested)
            {
                return;
            }
            if (wake.Task.IsCompleted)
            {
                wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            asked = wake.Task;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Task.WhenAny(Task.Delay(delay, time, stop.Token), asked);
        }
        finally
        {
            await stop.CancelAsync();
        }
        ct.ThrowIfCancellationRequested();
    }

    public void RoundStarted()
    {
        lock (sync)
        {
            running = true;
        }
    }

    /// <summary>
    /// A round ended. <paramref name="didNow"/> is what it got through and <paramref name="stillDue"/> what's left; with
    /// nothing left, or after an error, a request to run now is over.
    /// </summary>
    public void RoundEnded(int didNow, int? stillDue, DateTime nowUtc, string? error = null)
    {
        lock (sync)
        {
            running = false;
            done += didNow;
            due = stillDue;
            lastRunAtUtc = nowUtc;
            lastError = error;
            if (error is not null || stillDue is null or 0)
            {
                requested = false;
            }
        }
    }
}

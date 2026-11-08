using Assistant.API.Services;
using TvTracker;

namespace Assistant.API.Alerts;

/// <summary>
/// Fires when the oldest unanswered question was asked <see cref="MaxWait"/> or more ago, or <see cref="MaxUnanswered"/>
/// or more are waiting. A question takes 10 s to 2 minutes on a CPU, one at a time, so either means the model server is
/// stuck or down, or the app has outgrown one small model: people are watching a spinner (the page polls) meanwhile.
/// </summary>
public sealed class AskQueueAlert(AskQueue queue, TimeProvider time) : IAlertCheck
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);
    public const int MaxUnanswered = 10;

    public string Name => "ask-queue";

    public Task<AlertState> CheckAsync(CancellationToken ct)
    {
        var (unanswered, oldest) = queue.Backlog();
        return Task.FromResult(Evaluate(unanswered, oldest, time.GetUtcNow().UtcDateTime));
    }

    internal static AlertState Evaluate(int unanswered, DateTime? oldestAskedAtUtc, DateTime nowUtc)
    {
        if (oldestAskedAtUtc is not { } oldest)
        {
            return new AlertState(false, "No questions waiting");
        }
        var waited = nowUtc - oldest;
        return new AlertState(waited >= MaxWait || unanswered >= MaxUnanswered,
            $"{unanswered} question{(unanswered == 1 ? "" : "s")} unanswered, the oldest asked {(int)waited.TotalMinutes} min ago");
    }
}

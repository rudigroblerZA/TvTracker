using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Assistant.API.Services;

/// <summary>A question and, once it's answered, its answer. Kept in memory only.</summary>
public sealed class Ask
{
    public required Guid Id { get; init; }
    public required string UserId { get; init; }
    public required string Question { get; init; }

    /// <summary>The question this one follows up, for the conversation so far.</summary>
    public Guid? PreviousId { get; init; }

    public required DateTime AskedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Added to while the question is answered, and read by the page meanwhile.</summary>
    public ConcurrentQueue<ToolStep> Steps { get; } = new();
    public string? Answer { get; set; }
    public IReadOnlyList<ShowRef> Shows { get; set; } = [];
    public string? Problem { get; set; }

    public string Status => FinishedAtUtc is not null ? (Answer is not null ? "answered" : "failed") : StartedAtUtc is not null ? "thinking" : "queued";
}

/// <summary>
/// Questions are answered here, one at a time, rather than in the request: a small model on a CPU takes from seconds to
/// minutes per question, longer than the app's clients wait, and one model server shouldn't run several at once. The
/// page polls until the answer is in.
/// <list type="bullet">
/// <item><b>In memory, for <see cref="KeepFor"/>:</b> questions are a moment's help, not a record, so nothing is stored
/// and a restart forgets them (and drops any still queued).</item>
/// <item><b>Limits:</b> one question waiting per person, and <see cref="PerHour"/> an hour, so one person can't keep
/// the model busy for everyone.</item>
/// </list>
/// </summary>
public sealed class AskQueue(AnswerLoop loop, TimeProvider time, ILogger<AskQueue> logger) : BackgroundService
{
    public const int PerHour = 20;
    public const int MaxFollowUps = 2;
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<Guid, Ask> asks = new();
    private readonly Channel<Ask> queue = Channel.CreateUnbounded<Ask>();

    public enum Refusal { None, Busy, HourlyLimit }

    public (Ask? Ask, Refusal Refusal) Enqueue(string userId, string question, Guid? previousId)
    {
        Prune();
        var now = time.GetUtcNow().UtcDateTime;
        var mine = asks.Values.Where(a => a.UserId == userId).ToList();
        if (mine.Any(a => a.FinishedAtUtc is null))
        {
            return (null, Refusal.Busy);
        }
        if (mine.Count(a => a.AskedAtUtc > now.AddHours(-1)) >= PerHour)
        {
            return (null, Refusal.HourlyLimit);
        }

        var previous = previousId is { } id && Get(id, userId) is { Answer: not null } found ? found.Id : (Guid?)null;
        var ask = new Ask { Id = Guid.CreateVersion7(), UserId = userId, Question = question, PreviousId = previous, AskedAtUtc = now };
        asks[ask.Id] = ask;
        queue.Writer.TryWrite(ask);
        return (ask, Refusal.None);
    }

    /// <summary>The asker's own question; anyone else's reads as missing.</summary>
    public Ask? Get(Guid id, string userId) => asks.TryGetValue(id, out var ask) && ask.UserId == userId ? ask : null;

    /// <summary>Questions still waiting ahead of this one.</summary>
    public int Ahead(Ask ask) => asks.Values.Count(a => a.StartedAtUtc is null && a.AskedAtUtc < ask.AskedAtUtc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var ask in queue.Reader.ReadAllAsync(stoppingToken))
        {
            ask.StartedAtUtc = time.GetUtcNow().UtcDateTime;
            try
            {
                var answer = await loop.AnswerAsync(ask.Question, Earlier(ask), ask.Steps.Enqueue, stoppingToken);
                ask.Answer = answer.Text;
                ask.Shows = answer.Shows;
                ask.Problem = answer.Problem;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Answering a question failed");
                ask.Problem = "Something went wrong answering that. Try again.";
            }
            ask.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
            logger.LogInformation("Question {Outcome} in {Seconds:0.0} s with {Tools} tool calls", ask.Status,
                (ask.FinishedAtUtc - ask.StartedAtUtc)!.Value.TotalSeconds, ask.Steps.Count);
        }
    }

    private List<Turn> Earlier(Ask ask)
    {
        var turns = new List<Turn>();
        var previousId = ask.PreviousId;
        while (turns.Count < MaxFollowUps && previousId is { } id && Get(id, ask.UserId) is { Answer: { } answer } previous)
        {
            turns.Insert(0, new Turn(previous.Question, answer, previous.Shows));
            previousId = previous.PreviousId;
        }
        return turns;
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow().UtcDateTime - KeepFor;
        foreach (var old in asks.Values.Where(a => a.FinishedAtUtc < cutoff))
        {
            asks.TryRemove(old.Id, out _);
        }
    }
}

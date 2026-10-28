using System.Threading.Channels;
using Research.API.Data;

namespace Research.API.Services;

/// <summary>
/// Drafts run here, one at a time, rather than in the request: a small model on a CPU takes minutes, longer than the
/// app's clients wait (the standard resilience handler gives up at 30 s), and one model server shouldn't run several
/// drafts at once. The dossier says it's drafting until the result (or the problem) is stored; the page checks back.
/// A restart loses queued drafts, and a dossier left "drafting" for more than <see cref="Stale"/> can be drafted again.
/// </summary>
public sealed class DraftQueue(IServiceScopeFactory scopes, InsightDrafter drafter, TimeProvider time, ILogger<DraftQueue> logger) : BackgroundService
{
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);

    private readonly Channel<(Guid ShowId, List<(int Number, ResearchSource Source)> Sources, string Title)> queue =
        Channel.CreateUnbounded<(Guid, List<(int, ResearchSource)>, string)>();

    public void Enqueue(Guid showId, string title, List<(int Number, ResearchSource Source)> sources) => queue.Writer.TryWrite((showId, sources, title));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (showId, sources, title) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var (draft, problem) = await drafter.DraftAsync(title, sources, stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ResearchContext>();
                if (await db.Dossiers.FindAsync([showId], stoppingToken) is { } dossier)
                {
                    dossier.Draft = draft ?? dossier.Draft;
                    dossier.DraftProblem = problem;
                    dossier.DraftingSinceUtc = null;
                    dossier.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't store the draft for {Show}", title);
            }
        }
    }
}

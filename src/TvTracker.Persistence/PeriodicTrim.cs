using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker.Persistence;

/// <summary>
/// A background job that keeps tables bounded: every <see cref="Interval"/> (first after <see cref="FirstRunDelay"/>)
/// it deletes old rows in batches of <see cref="Batch"/>, a fresh scope per batch so a long first trim doesn't grow a
/// change tracker or hold a long lock, until every kind of row comes up short of a batch. A failed run is logged and
/// tried again next interval.
/// </summary>
public abstract class PeriodicTrim(IServiceScopeFactory scopes, TimeProvider time, ILogger logger) : BackgroundService
{
    public const int Batch = 5000;

    protected abstract TimeSpan Interval { get; }

    protected abstract TimeSpan FirstRunDelay { get; }

    /// <summary>
    /// Deletes up to <see cref="Batch"/> rows of each kind; returns how many of each went, by name.
    /// </summary>
    protected abstract Task<IReadOnlyList<(string Rows, int Deleted)>> TrimBatchAsync(IServiceProvider services, DateTime nowUtc, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstRunDelay, time, stoppingToken);
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await TrimAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Job} failed; trying again in {Interval}", GetType().Name, Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var totals = new Dictionary<string, int>();
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var deleted = await TrimBatchAsync(scope.ServiceProvider, now, ct);
            foreach (var (rows, count) in deleted)
            {
                totals[rows] = totals.GetValueOrDefault(rows) + count;
            }
            if (deleted.All(d => d.Deleted < Batch))
            {
                break;
            }
        }
        if (totals.Values.Any(t => t > 0))
        {
            logger.LogInformation("Trimmed {Counts}", string.Join(", ", totals.Select(t => $"{t.Value} {t.Key}")));
        }
    }
}

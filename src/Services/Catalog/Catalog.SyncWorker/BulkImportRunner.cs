using Catalog.SyncWorker.Providers;

namespace Catalog.SyncWorker;

/// <summary>
/// What to import: exactly one of titles (each imports the provider's best match), provider ids, or the top N.
/// </summary>
public record BulkImportRequest(IReadOnlyList<string>? Titles = null, IReadOnlyList<string>? Ids = null, int? Top = null);

/// <param name="State">"running", "completed", "cancelled" or "failed".</param>
/// <param name="Stage">What a running job is doing, e.g. reading the provider's index before a top-N import.</param>
/// <param name="LastImported">The title of the show imported most recently.</param>
/// <param name="Problems">Items that weren't imported and why (titles with no match, unknown ids, errors), up to 200.</param>
public record BulkImportStatus(
    Guid Id,
    string State,
    string? Stage,
    int Total,
    int Processed,
    int Imported,
    int AlreadyInCatalog,
    int NotFound,
    int Failed,
    string? LastImported,
    IReadOnlyList<BulkImportProblem> Problems,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    string? Error);

public record BulkImportProblem(string Item, string Reason);

/// <summary>
/// Runs one bulk import at a time in the background, for the bulk import CLI. Shows are imported one after another
/// through the same synchronizer and provider rate limit as the hourly sync, so a large import takes a while
/// (TVmaze allows about two shows a second) but can't starve the rest of the worker. Job state is kept in memory:
/// a restart ends the job, and running it again skips the shows already imported.
/// </summary>
public sealed class BulkImportRunner(
    IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<BulkImportRunner> logger)
{
    public const int MaxItems = 5000;
    private const int MaxProblems = 200;

    private readonly Lock _lock = new();
    private Job? _job;

    public BulkImportStatus? Current
    {
        get
        {
            lock (_lock)
            {
                return _job?.Snapshot();
            }
        }
    }

    /// <summary>
    /// Starts a job, or returns the running one's status (and false) when one is already running.
    /// </summary>
    public (bool Started, BulkImportStatus Status) TryStart(BulkImportRequest request)
    {
        lock (_lock)
        {
            if (_job is { IsRunning: true } running)
            {
                return (false, running.Snapshot());
            }

            var job = new Job(Guid.NewGuid(), time.GetUtcNow().UtcDateTime, CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping));
            _job = job;
            _ = Task.Run(() => RunAsync(job, request));
            return (true, job.Snapshot());
        }
    }

    /// <summary>
    /// Asks the job to stop after the show it's importing. False when it isn't running (or isn't the current job).
    /// </summary>
    public bool TryCancel(Guid id)
    {
        lock (_lock)
        {
            if (_job is not { IsRunning: true } job || job.Id != id)
            {
                return false;
            }

            job.Cancellation.Cancel();
            return true;
        }
    }

    private async Task RunAsync(Job job, BulkImportRequest request)
    {
        var ct = job.Cancellation.Token;
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
                if (request.Top is { } top)
                {
                    job.Update(j => j.Stage = $"Reading {provider.Name}'s show index");
                    var ids = await provider.GetPopularShowIdsAsync(top,
                        pages => job.Update(j => j.Stage = $"Reading {provider.Name}'s show index (page {pages})"), ct);
                    await ImportAllAsync(job, ids.Select(id => new Item(id, IsTitle: false)).ToList(), ct);
                }
                else
                {
                    var items = request.Titles?.Select(t => new Item(t, IsTitle: true)) ?? request.Ids!.Select(id => new Item(id, IsTitle: false));
                    await ImportAllAsync(job, items.ToList(), ct);
                }
            }

            job.Finish("completed", time);
            logger.LogInformation("Bulk import {Id} completed: {Status}", job.Id, job.Snapshot());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Finish("cancelled", time);
            logger.LogInformation("Bulk import {Id} cancelled", job.Id);
        }
        catch (Exception ex)
        {
            job.Finish("failed", time, ex.Message);
            logger.LogError(ex, "Bulk import {Id} failed", job.Id);
        }
        finally
        {
            // Under the lock TryCancel takes, so it can't cancel a disposed source.
            lock (_lock)
            {
                job.Cancellation.Dispose();
            }
        }
    }

    private async Task ImportAllAsync(Job job, List<Item> items, CancellationToken ct)
    {
        job.Update(j =>
        {
            j.Stage = "Importing";
            j.Total = items.Count;
        });

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // A scope per show keeps the change tracker small however many shows the job imports.
            await using var scope = scopeFactory.CreateAsyncScope();
            var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
            var synchronizer = scope.ServiceProvider.GetRequiredService<CatalogSynchronizer>();
            try
            {
                var externalId = item.IsTitle ? (await provider.SearchShowIdsAsync(item.Value, 1, ct)).FirstOrDefault() : item.Value;
                var result = externalId is null ? new ImportResult(ImportOutcome.NotFound, null) : await synchronizer.ImportShowAsync(externalId, ct);
                job.Update(j =>
                {
                    switch (result.Outcome)
                    {
                        case ImportOutcome.Imported:
                            j.Imported++;
                            j.LastImported = result.Title;
                            break;
                        case ImportOutcome.AlreadyInCatalog:
                            j.AlreadyInCatalog++;
                            break;
                        default:
                            j.NotFound++;
                            j.AddProblem(item.Value, item.IsTitle ? "no match" : "no show with this id");
                            break;
                    }
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad show shouldn't end the job; the hourly sync or a rerun picks it up.
                logger.LogWarning(ex, "Bulk import {Id}: importing {Item} failed", job.Id, item.Value);
                job.Update(j =>
                {
                    j.Failed++;
                    j.AddProblem(item.Value, ex.Message);
                });
            }
            finally
            {
                job.Update(j => j.Processed++);
            }
        }
    }

    private sealed record Item(string Value, bool IsTitle);

    private sealed class Job(Guid id, DateTime startedAtUtc, CancellationTokenSource cancellation)
    {
        private readonly Lock _lock = new();
        private readonly List<BulkImportProblem> _problems = [];

        public Guid Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool IsRunning { get { lock (_lock) { return State == "running"; } } }

        public string State { get; private set; } = "running";
        public string? Stage { get; set; } = "Starting";
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Imported { get; set; }
        public int AlreadyInCatalog { get; set; }
        public int NotFound { get; set; }
        public int Failed { get; set; }
        public string? LastImported { get; set; }
        private DateTime? _finishedAtUtc;
        private string? _error;

        public void AddProblem(string item, string reason)
        {
            if (_problems.Count < MaxProblems)
            {
                _problems.Add(new BulkImportProblem(item, reason));
            }
        }

        public void Update(Action<Job> change)
        {
            lock (_lock)
            {
                change(this);
            }
        }

        public void Finish(string state, TimeProvider time, string? error = null)
        {
            lock (_lock)
            {
                State = state;
                Stage = null;
                _finishedAtUtc = time.GetUtcNow().UtcDateTime;
                _error = error;
            }
        }

        public BulkImportStatus Snapshot()
        {
            lock (_lock)
            {
                return new BulkImportStatus(Id, State, Stage, Total, Processed, Imported, AlreadyInCatalog, NotFound, Failed,
                    LastImported, _problems.ToList(), startedAtUtc, _finishedAtUtc, _error);
            }
        }
    }
}

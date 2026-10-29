using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using System.Security.Cryptography;
using System.Text;

namespace Catalog.SyncWorker;

/// <summary>
/// Keeps a vector for every show, for semantic search. Every 30 seconds it embeds shows that are new, were synced
/// since they were embedded, or were embedded by another model, most popular first. A sync that changes none of the
/// embedded text only updates the timestamp. On first start this backfills the whole catalog.
/// </summary>
public sealed class ShowEmbeddingIndexer(
    IServiceScopeFactory scopeFactory,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    EmbeddingModel model,
    TimeProvider time,
    ILogger<ShowEmbeddingIndexer> logger) : BackgroundService
{
    private const int BatchSize = 32;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var embedded = 0;
                int count;
                while ((count = await IndexBatchAsync(stoppingToken)) > 0)
                {
                    embedded += count;
                }

                if (embedded > 0)
                {
                    logger.LogInformation("Embedded {Count} shows for semantic search", embedded);
                }
            }
            catch (EmbeddingsUnavailableException)
            {
                // The model is still downloading or failed to load (logged by the generator); try again next round.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Embedding shows failed; retrying in {Interval}", Interval);
            }

            await Task.Delay(Interval, time, stoppingToken);
        }
    }

    /// <summary>Embeds one batch of shows that need it and returns how many it looked at (0 when all are current).</summary>
    private async Task<int> IndexBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        // Taken before reading, so a show synced while this batch runs is picked up again next round.
        var now = time.GetUtcNow().UtcDateTime;

        var shows = await db.Shows.AsNoTracking()
            .Where(s => !db.ShowEmbeddings.Any(e =>
                e.ShowId == s.Id && e.Model == model.Id && e.EmbeddedAtUtc >= s.LastSyncedAtUtc))
            .OrderByDescending(s => s.Popularity)
            .Take(BatchSize)
            .Select(s => new { s.Id, s.Title, s.Genres, s.Network, s.Overview })
            .ToListAsync(ct);
        if (shows.Count == 0)
        {
            return 0;
        }

        var ids = shows.Select(s => s.Id).ToList();
        var existing = await db.ShowEmbeddings.Where(e => ids.Contains(e.ShowId)).ToDictionaryAsync(e => e.ShowId, ct);

        var texts = shows.ToDictionary(s => s.Id, s => EmbeddingText(s.Title, s.Genres, s.Network, s.Overview));
        var changed = shows
            .Where(s => !existing.TryGetValue(s.Id, out var e) || e.Model != model.Id || e.SourceHash != Hash(texts[s.Id]))
            .Select(s => s.Id)
            .ToList();

        var vectors = changed.Count == 0 ? [] : await generator.GenerateAsync(changed.Select(id => texts[id]), cancellationToken: ct);

        foreach (var show in shows)
        {
            if (!existing.TryGetValue(show.Id, out var embedding))
            {
                embedding = new ShowEmbedding { ShowId = show.Id, Model = "", Embedding = new Vector(new float[ShowEmbedding.Dimensions]), SourceHash = "" };
                db.ShowEmbeddings.Add(embedding);
            }

            var index = changed.IndexOf(show.Id);
            if (index >= 0)
            {
                embedding.Model = model.Id;
                embedding.Embedding = new Vector(vectors[index].Vector);
                embedding.SourceHash = Hash(texts[show.Id]);
            }
            embedding.EmbeddedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return shows.Count;
    }

    /// <summary>What a show's vector represents: what people would describe when looking for it.</summary>
    internal static string EmbeddingText(string title, IReadOnlyList<string> genres, string? network, string? overview)
    {
        var text = new StringBuilder(title).Append('.');
        if (genres.Count > 0)
        {
            text.Append(' ').Append(string.Join(", ", genres)).Append('.');
        }
        if (!string.IsNullOrWhiteSpace(network))
        {
            text.Append(" On ").Append(network).Append('.');
        }
        if (!string.IsNullOrWhiteSpace(overview))
        {
            text.Append(' ').Append(overview);
        }
        return text.ToString();
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

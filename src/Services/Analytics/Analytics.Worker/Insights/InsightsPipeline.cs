using System.Text.Json;
using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Analytics.Worker.Insights;

/// <summary>
/// Computes every show's insights document, the lists built across shows and the actor insights, from Analytics'
/// copies of the catalog and our users' activity. Runs hourly; the API only reads what this writes.
/// <list type="number">
/// <item>Loads the <see cref="InsightSources"/>, then the shows in batches of <see cref="ShowBatchSize"/>.</item>
/// <item>Builds each show's document by running the <see cref="IShowInsight"/> stages in order.</item>
/// <item>Stores the document, and notes the show when its tags changed.</item>
/// <item>Hands the finished document to every <see cref="IListInsight"/>.</item>
/// <item>Publishes the changed tags, then replaces every list in one transaction.</item>
/// </list>
/// The insights only compute; loading, batching, storage and events all happen here.
/// </summary>
public sealed class InsightsPipeline(
    AnalyticsContext db,
    IIntegrationEventOutbox outbox,
    IEnumerable<IShowInsight> stages,
    IEnumerable<IListInsight> lists,
    TimeProvider time,
    ILogger<InsightsPipeline> logger)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int ShowBatchSize = 100;

    private static readonly RunFacts NoRun = new(0, 0, 0, null, null, null);

    /// <param name="publishAllTags">Send every show's tags to the catalog, not just the ones that changed.</param>
    /// <returns>How many shows were computed.</returns>
    public async Task<int> RunAsync(CancellationToken ct, bool publishAllTags = false)
    {
        var sources = await InsightSources.LoadAsync(db, time.GetUtcNow(), ct);
        var builders = lists.Select(l => l.Begin(sources)).ToList();
        var tagChanges = new List<ShowTagKeys>();

        foreach (var batch in sources.Shows.Keys.Chunk(ShowBatchSize))
        {
            var ids = batch.ToList();
            var existing = await db.ShowInsights.Where(i => ids.Contains(i.ShowId)).ToDictionaryAsync(i => i.ShowId, ct);

            foreach (var show in await sources.LoadShowsAsync(db, ids, ct))
            {
                var insights = stages.Aggregate(Blank(show), (document, stage) => stage.Apply(show, document));

                var document = JsonSerializer.Serialize(insights, JsonOptions);
                var tagKeys = (insights.Tags ?? []).Select(t => t.Key).ToList();
                existing.TryGetValue(show.ShowId, out var row);
                if (publishAllTags || row is null || !TagKeysOf(row.Document).SequenceEqual(tagKeys))
                {
                    tagChanges.Add(new ShowTagKeys(show.ShowId, tagKeys));
                }

                if (row is not null)
                {
                    row.Document = document;
                    row.ComputedAtUtc = sources.ComputedAtUtc;
                }
                else
                {
                    db.ShowInsights.Add(new ShowInsightsRow { ShowId = show.ShowId, Document = document, ComputedAtUtc = sources.ComputedAtUtc });
                }

                foreach (var builder in builders)
                {
                    builder.Add(show, insights);
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await PublishTagsAsync(tagChanges, ct);
        await ReplaceListsAsync(builders, ct);

        logger.LogInformation("Computed insights for {Shows} shows; lists: {Lists}",
            sources.Shows.Count, string.Join(", ", builders.Select(b => $"{b.Name} {b.Count}")));
        return sources.Shows.Count;
    }

    /// <summary>The document the stages start from: the show, its overall rating, and nothing else yet.</summary>
    private static ShowInsights Blank(ShowInsightContext show) =>
        new(show.ShowId, show.Show.Title, show.Sources.ComputedAtUtc, show.Rating, [], null, null, null, [], NoRun, null, []);

    /// <summary>Sends the changed tags to the catalog through the outbox, in events of at most <see cref="ShowTagsChangedIntegrationEvent.MaxShows"/> shows.</summary>
    private async Task PublishTagsAsync(List<ShowTagKeys> changes, CancellationToken ct)
    {
        foreach (var chunk in changes.Chunk(ShowTagsChangedIntegrationEvent.MaxShows))
        {
            outbox.Add(new ShowTagsChangedIntegrationEvent(chunk));
        }

        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await outbox.PublishAddedAsync(ct);
            logger.LogInformation("Published tags for {Shows} shows", changes.Count);
        }
    }

    /// <summary>The tag keys in a stored insights document (none in documents from before tags).</summary>
    private static List<string> TagKeysOf(string document)
    {
        using var json = JsonDocument.Parse(document);
        return json.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Select(t => t.GetProperty("key").GetString()!).ToList()
            : [];
    }

    private Task ReplaceListsAsync(List<ListBuilder> builders, CancellationToken ct) =>
        // One transaction, so readers never see an empty list mid-refresh. Run through the execution strategy:
        // Aspire's Npgsql integration retries on transient errors and refuses transactions started outside it.
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var builder in builders)
            {
                await builder.ReplaceAsync(db, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            db.ChangeTracker.Clear();
        });
}

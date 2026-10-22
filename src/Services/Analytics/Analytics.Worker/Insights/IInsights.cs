using System.Text.Json;
using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;

namespace Analytics.Worker.Insights;

/// <summary>
/// One part of a show's insights document (<see cref="ShowInsights"/>). The pipeline runs every registered stage in
/// registration order, each given the document so far, and stores what the last one returns. Stages compute; they
/// don't load, save or publish anything.
/// </summary>
public interface IShowInsight
{
    ShowInsights Apply(ShowInsightContext show, ShowInsights insights);
}

/// <summary>
/// One rule of the show tags (see <see cref="ShowTags"/>): the tag, or null when the show doesn't qualify. The tags
/// stage asks every registered rule in registration order, which is the order tags appear in.
/// </summary>
public interface IShowTagRule
{
    ShowTag? TagFor(ShowInsightContext show);
}

/// <summary>
/// A table built across all shows (premieres, top episodes, discovery lists, actor insights). Each run starts a fresh
/// <see cref="ListBuilder"/>, which sees every show's finished document and then replaces the table's rows.
/// </summary>
public interface IListInsight
{
    ListBuilder Begin(InsightSources sources);
}

/// <summary>A list being built during one run.</summary>
public abstract class ListBuilder
{
    /// <summary>For the log: "premieres", "top-episodes".</summary>
    public abstract string Name { get; }

    /// <summary>Rows written by the last <see cref="ReplaceAsync"/>.</summary>
    public int Count { get; protected set; }

    /// <summary>Called once per show, in batches, after the show's document is complete.</summary>
    public abstract void Add(ShowInsightContext show, ShowInsights insights);

    /// <summary>
    /// Deletes the table's rows and adds the new ones. Runs inside the pipeline's transaction, which saves and
    /// commits every list together, so readers never see a list empty mid-refresh.
    /// </summary>
    public abstract Task ReplaceAsync(AnalyticsContext db, CancellationToken ct);
}

/// <summary>A list stored in one table, replaced whole each run.</summary>
public abstract class ListBuilder<TRow>(Func<AnalyticsContext, DbSet<TRow>> table) : ListBuilder where TRow : class
{
    /// <summary>The rows to store, once every show has been added.</summary>
    protected abstract IReadOnlyCollection<TRow> Rows();

    public override async Task ReplaceAsync(AnalyticsContext db, CancellationToken ct)
    {
        var rows = Rows();
        await table(db).ExecuteDeleteAsync(ct);
        table(db).AddRange(rows);
        Count = rows.Count;
    }
}

/// <summary>
/// A part of the show document that another service keeps a copy of: tags for the catalog's search filter, DNA for
/// recommendations. Each run starts a fresh <see cref="PublicationBuilder"/>, which compares every show's new document
/// with the stored one; the pipeline publishes what changed through the outbox.
/// </summary>
public interface IInsightPublication
{
    /// <param name="publishAll">Send every show's value, not only changed ones (after start and daily, so lost events heal).</param>
    PublicationBuilder Begin(bool publishAll);
}

/// <summary>A publication being collected during one run.</summary>
public abstract class PublicationBuilder
{
    /// <summary>For the log: "tags", "DNA".</summary>
    public abstract string Name { get; }

    /// <summary>Shows sent by the last <see cref="Events"/>.</summary>
    public int Count { get; protected set; }

    /// <param name="previous">The show's stored document from the last run; null for a show computed for the first time.</param>
    public abstract void Add(ShowInsights insights, JsonElement? previous);

    public abstract IReadOnlyList<IntegrationEvent> Events();
}

/// <summary>A publication of one value per show, sent in events of at most <paramref name="maxPerEvent"/> shows.</summary>
public abstract class PublicationBuilder<TValue>(bool publishAll, int maxPerEvent) : PublicationBuilder where TValue : class
{
    private readonly List<TValue> changed = [];

    protected abstract TValue ValueOf(ShowInsights insights);

    /// <summary>The value in a stored document.</summary>
    protected abstract TValue PreviousValueOf(JsonElement document);

    protected abstract bool Same(TValue previous, TValue current);

    protected abstract IntegrationEvent EventFor(IReadOnlyList<TValue> values);

    public override void Add(ShowInsights insights, JsonElement? previous)
    {
        var value = ValueOf(insights);
        if (publishAll || previous is not { } document || !Same(PreviousValueOf(document), value))
        {
            changed.Add(value);
        }
    }

    public override IReadOnlyList<IntegrationEvent> Events()
    {
        Count = changed.Count;
        return changed.Chunk(maxPerEvent).Select(chunk => EventFor(chunk)).ToList();
    }
}

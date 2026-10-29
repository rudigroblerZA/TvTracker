using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
    /// Makes the table's rows the new ones. Runs inside the pipeline's transaction, which saves and commits every
    /// list together, so readers never see a list empty mid-refresh.
    /// </summary>
    public abstract Task ReplaceAsync(AnalyticsContext db, CancellationToken ct);
}

/// <summary>
/// A list stored in one table. Each run's rows are compared with the stored ones by primary key: changed rows are
/// updated, new ones added, missing ones deleted, and an unchanged row isn't written at all. Deleting and re-inserting
/// every row each hour (~15,000 across the lists, most of them the same hour to hour) left Postgres a table's worth
/// of dead tuples per run for autovacuum to clear. "Computed at" stamps (on the row and inside its document) are left
/// out of the comparison, so a row whose values haven't moved keeps the time they were last computed. A jsonb document
/// is compared as JSON with its keys sorted: Postgres stores jsonb in its own key order and spacing, so the text read
/// back never equals the compact text written (which made every document look changed).
/// </summary>
public abstract class ListBuilder<TRow>(Func<AnalyticsContext, DbSet<TRow>> table) : ListBuilder where TRow : class
{
    private static readonly JsonSerializerOptions Comparison = new(JsonSerializerDefaults.Web);

    private const string ComputedAt = "ComputedAtUtc";

    /// <summary>The row's values as one comparable string: every mapped property but the computed-at stamp, documents canonical.</summary>
    private static string Comparable(TRow row, IEnumerable<IProperty> properties)
    {
        var text = new StringBuilder();
        foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.Name.Equals(ComputedAt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = property.PropertyInfo!.GetValue(row);
            text.Append(property.Name).Append('=');
            if (property.GetColumnType() == "jsonb" && value is string json)
            {
                Canonical(JsonNode.Parse(json), text);
            }
            else
            {
                text.Append(JsonSerializer.Serialize(value, Comparison));
            }
            text.Append(';');
        }
        return text.ToString();
    }

    /// <summary>Compact JSON with object keys sorted, computed-at stamps left out at any depth.</summary>
    private static void Canonical(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case JsonObject o:
                text.Append('{');
                foreach (var (key, value) in o.Where(p => !p.Key.Equals(ComputedAt, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    text.Append(key).Append(':');
                    Canonical(value, text);
                    text.Append(',');
                }
                text.Append('}');
                break;
            case JsonArray a:
                text.Append('[');
                foreach (var item in a)
                {
                    Canonical(item, text);
                    text.Append(',');
                }
                text.Append(']');
                break;
            default:
                text.Append(node?.ToJsonString() ?? "null");
                break;
        }
    }

    /// <summary>The rows to store, once every show has been added.</summary>
    protected abstract IReadOnlyCollection<TRow> Rows();

    public override async Task ReplaceAsync(AnalyticsContext db, CancellationToken ct)
    {
        var rows = Rows();
        var entity = db.Model.FindEntityType(typeof(TRow))!;
        var key = entity.FindPrimaryKey()!.Properties;
        var properties = entity.GetProperties().ToList();
        string KeyOf(TRow row) => string.Join('|', key.Select(p => p.PropertyInfo!.GetValue(row)));

        var stored = (await table(db).ToListAsync(ct)).ToDictionary(KeyOf);
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var k = KeyOf(row);
            if (!seen.Add(k))
            {
                continue;
            }
            if (!stored.TryGetValue(k, out var existing))
            {
                table(db).Add(row);
            }
            else if (Comparable(existing, properties) != Comparable(row, properties))
            {
                db.Entry(existing).CurrentValues.SetValues(row);
            }
        }
        table(db).RemoveRange(stored.Where(s => !seen.Contains(s.Key)).Select(s => s.Value));
        Count = seen.Count;
    }
}

/// <summary>
/// A part of the show document that another service hears about: tags for the catalog's search filter, DNA for
/// recommendations, a season's verdict for notifications. Each run starts a fresh <see cref="PublicationBuilder"/>,
/// which compares every show's new document with the stored one; after each batch the pipeline adds the batch's events
/// to the outbox in the same save as its documents, so a stored document's events are never lost.
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

    /// <summary>Shows sent so far this run.</summary>
    public int Count { get; protected set; }

    /// <param name="previous">The show's stored document from the last run; null for a show computed for the first time.</param>
    public abstract void Add(ShowInsights insights, JsonElement? previous);

    /// <summary>The events for the shows added since the last call; the pipeline saves them with those shows' documents.</summary>
    public abstract IReadOnlyList<IntegrationEvent> TakeEvents();
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

    public override IReadOnlyList<IntegrationEvent> TakeEvents()
    {
        Count += changed.Count;
        var events = changed.Chunk(maxPerEvent).Select(chunk => EventFor(chunk)).ToList();
        changed.Clear();
        return events;
    }
}

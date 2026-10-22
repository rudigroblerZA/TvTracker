using Analytics.Data;
using Microsoft.EntityFrameworkCore;

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

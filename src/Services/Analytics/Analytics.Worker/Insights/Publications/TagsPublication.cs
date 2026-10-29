using Analytics.Data;
using System.Text.Json;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>Show tag keys, for the catalog's search filter (<see cref="ShowTagsChangedIntegrationEvent"/>).</summary>
public sealed class TagsPublication : IInsightPublication
{
    public PublicationBuilder Begin(bool publishAll) => new Builder(publishAll);

    private sealed class Builder(bool publishAll) : PublicationBuilder<ShowTagKeys>(publishAll, ShowTagsChangedIntegrationEvent.MaxShows)
    {
        public override string Name => "tags";

        protected override ShowTagKeys ValueOf(ShowInsights insights) =>
            new(insights.ShowId, (insights.Tags ?? []).Select(t => t.Key).ToList());

        /// <summary>No tags in documents from before tags.</summary>
        protected override ShowTagKeys PreviousValueOf(JsonElement document) =>
            new(Guid.Empty, document.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(t => t.GetProperty("key").GetString()!).ToList()
                : []);

        protected override bool Same(ShowTagKeys previous, ShowTagKeys current) => previous.Tags.SequenceEqual(current.Tags);

        protected override IntegrationEvent EventFor(IReadOnlyList<ShowTagKeys> values) => new ShowTagsChangedIntegrationEvent(values);
    }
}

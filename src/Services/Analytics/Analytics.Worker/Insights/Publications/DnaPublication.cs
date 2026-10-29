using System.Text.Json;
using Analytics.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>Show DNA, for Recommendations (<see cref="ShowDnaChangedIntegrationEvent"/>). A show without DNA is sent with no traits.</summary>
public sealed class DnaPublication : IInsightPublication
{
    public PublicationBuilder Begin(bool publishAll) => new Builder(publishAll);

    private sealed class Builder(bool publishAll) : PublicationBuilder<ShowDnaValues>(publishAll, ShowDnaChangedIntegrationEvent.MaxShows)
    {
        public override string Name => "DNA";

        protected override ShowDnaValues ValueOf(ShowInsights insights) =>
            new(insights.ShowId, (insights.Dna?.Traits ?? []).ToDictionary(t => t.Key, t => t.Value));

        /// <summary>Documents from before DNA have none; a show that now has DNA is then sent.</summary>
        protected override ShowDnaValues PreviousValueOf(JsonElement document) =>
            new(Guid.Empty, document.TryGetProperty(InsightDocuments.Paths.Dna, out var dna) && dna.ValueKind == JsonValueKind.Object
                ? dna.GetProperty(InsightDocuments.Paths.Traits).EnumerateArray().ToDictionary(t => t.GetProperty(InsightDocuments.Paths.Key).GetString()!, t => t.GetProperty(InsightDocuments.Paths.Value).GetDouble())
                : new Dictionary<string, double>());

        protected override bool Same(ShowDnaValues previous, ShowDnaValues current) =>
            previous.Traits.Count == current.Traits.Count
            && current.Traits.All(t => previous.Traits.TryGetValue(t.Key, out var value) && value == t.Value);

        protected override IntegrationEvent EventFor(IReadOnlyList<ShowDnaValues> values) => new ShowDnaChangedIntegrationEvent(values);
    }
}

using System.Text.Json;
using Analytics.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>
/// A season's verdict, sent once for Notifications (<see cref="SeasonVerdictIntegrationEvent"/>): when a show's document
/// has a verdict for a season its stored document didn't. Unlike tags and DNA it's news, so a full send after start
/// doesn't repeat it, and only a verdict within <see cref="FreshFor"/> of when it could first be given
/// (<see cref="SeasonVerdict.ReadyAtUtc"/>) is sent: the first run after this shipped would otherwise have told followers
/// about every season that ended in the last six weeks.
/// </summary>
public sealed class SeasonVerdictPublication : IInsightPublication
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    public PublicationBuilder Begin(bool publishAll) => new Builder();

    private sealed class Builder : PublicationBuilder
    {
        private readonly List<IntegrationEvent> events = [];

        public override string Name => "season verdicts";

        public override void Add(ShowInsights insights, JsonElement? previous)
        {
            if (insights.SeasonVerdict is not { } verdict || insights.ComputedAtUtc - verdict.ReadyAtUtc > FreshFor
                || PreviousSeason(previous) == verdict.Number)
            {
                return;
            }
            events.Add(new SeasonVerdictIntegrationEvent(insights.ShowId, insights.Title, verdict.Number, verdict.Verdict, verdict.Rating,
                verdict.OthersMedian, verdict.Finale, verdict.FinaleRating, verdict.Started, verdict.Finished));
        }

        public override IReadOnlyList<IntegrationEvent> Events()
        {
            Count = events.Count;
            return events;
        }

        /// <summary>The season the stored document had a verdict for, if any.</summary>
        private static int? PreviousSeason(JsonElement? previous) =>
            previous is { } document && document.TryGetProperty("seasonVerdict", out var stored) && stored.ValueKind == JsonValueKind.Object
                ? stored.GetProperty("number").GetInt32()
                : null;
    }
}

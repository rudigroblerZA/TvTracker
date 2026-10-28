using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Analytics.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>
/// A season's verdict for Notifications (<see cref="SeasonVerdictIntegrationEvent"/>), sent once: when a show's document
/// has a verdict for a season its stored document didn't. The pipeline saves the event with the document, so it can't be
/// lost to a failed run. It's news rather than state, so a full send after start doesn't repeat it.
/// <list type="bullet">
/// <item><b>Fresh only</b> (within <see cref="FreshFor"/> of <see cref="SeasonVerdict.ReadyAtUtc"/>): a first run over
/// an empty store, or after a long outage, would otherwise tell followers about every season of the last six weeks.</item>
/// <item><b>Same season again:</b> a verdict that drops out of the document for a run (its ratings briefly missing) and
/// comes back is sent again; its <see cref="SeasonVerdictIntegrationEvent.VerdictId"/>, the same for a show and
/// season, lets Notifications tell followers once.</item>
/// </list>
/// </summary>
public sealed class SeasonVerdictPublication : IInsightPublication
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(2);

    public PublicationBuilder Begin(bool publishAll) => new Builder();

    /// <summary>Stable for a show and season, so every send of its verdict is the same notification.</summary>
    private static Guid VerdictId(Guid showId, int season) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"season-verdict:{showId}:{season}"))[..16]);

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
                verdict.OthersMedian, verdict.Finale, verdict.FinaleRating, verdict.Started, verdict.Finished,
                VerdictId(insights.ShowId, verdict.Number)));
        }

        public override IReadOnlyList<IntegrationEvent> TakeEvents()
        {
            Count += events.Count;
            var taken = events.ToList();
            events.Clear();
            return taken;
        }

        /// <summary>The season the stored document had a verdict for, if any.</summary>
        private static int? PreviousSeason(JsonElement? previous) =>
            previous is { } document && document.TryGetProperty("seasonVerdict", out var stored) && stored.ValueKind == JsonValueKind.Object
                ? stored.GetProperty("number").GetInt32()
                : null;
    }
}

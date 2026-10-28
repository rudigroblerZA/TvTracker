using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Analytics.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>
/// A season's verdict for Notifications (<see cref="SeasonVerdictIntegrationEvent"/>), sent on every run while it's
/// within <see cref="FreshFor"/> of when it could first be given (<see cref="SeasonVerdict.ReadyAtUtc"/>), each time with
/// the same <see cref="SeasonVerdictIntegrationEvent.VerdictId"/>; Notifications tells followers once.
/// <list type="bullet">
/// <item><b>Why every run:</b> the pipeline saves documents before it publishes, so "send when the stored document
/// lacked it" lost the verdict for good when a run failed in between, and sent it twice when a verdict dropped out of
/// the document for a run and came back.</item>
/// <item><b>Why fresh only:</b> without it, the first run after this shipped would have told followers about every
/// season that ended in the last six weeks.</item>
/// </list>
/// </summary>
public sealed class SeasonVerdictPublication : IInsightPublication
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    public PublicationBuilder Begin(bool publishAll) => new Builder();

    /// <summary>Stable for a show and season, so every send of its verdict is the same notification.</summary>
    public static Guid VerdictId(Guid showId, int season) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"season-verdict:{showId}:{season}"))[..16]);

    private sealed class Builder : PublicationBuilder
    {
        private readonly List<IntegrationEvent> events = [];

        public override string Name => "season verdicts";

        public override void Add(ShowInsights insights, JsonElement? previous)
        {
            if (insights.SeasonVerdict is not { } verdict || insights.ComputedAtUtc - verdict.ReadyAtUtc > FreshFor)
            {
                return;
            }
            events.Add(new SeasonVerdictIntegrationEvent(insights.ShowId, insights.Title, verdict.Number, verdict.Verdict, verdict.Rating,
                verdict.OthersMedian, verdict.Finale, verdict.FinaleRating, verdict.Started, verdict.Finished,
                VerdictId(insights.ShowId, verdict.Number)));
        }

        public override IReadOnlyList<IntegrationEvent> Events()
        {
            Count = events.Count;
            return events;
        }
    }
}

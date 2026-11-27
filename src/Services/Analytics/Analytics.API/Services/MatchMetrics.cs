using System.Diagnostics.Metrics;

namespace Analytics.API.Services;

/// <summary>
/// Counts for judging Taste Match as a way of bringing people in: links started, links answered (by a guest or a
/// member), and guests who then made an account and kept their result. Counts only; nobody is named.
/// </summary>
public sealed class MatchMetrics
{
    public const string MeterName = "TvTracker.Analytics.TasteMatch";

    private readonly Counter<long> started;
    private readonly Counter<long> answered;
    private readonly Counter<long> claimed;

    public MatchMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        started = meter.CreateCounter<long>("matches.started", description: "Taste Match links a member started");
        answered = meter.CreateCounter<long>("matches.answered", description: "Taste Match links a friend answered, by whether they were a guest or a member");
        claimed = meter.CreateCounter<long>("matches.claimed", description: "Guests who made an account and attached their match to it");
    }

    public void Started() => started.Add(1);

    public void Answered(bool guest) => answered.Add(1, new KeyValuePair<string, object?>("friend", guest ? "guest" : "member"));

    public void Claimed() => claimed.Add(1);
}

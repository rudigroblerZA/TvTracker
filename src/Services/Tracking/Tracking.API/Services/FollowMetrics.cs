using System.Diagnostics.Metrics;
using TvTracker.IntegrationEvents;

namespace Tracking.API.Services;

/// <summary>
/// Counts new follows by where they started (<see cref="FollowSources"/>), so a discovery tool can be judged by the
/// follows it leads to: <c>follows.started{source="blend"}</c> against the same tool's requests. A count only; who
/// followed what is not part of it.
/// </summary>
public sealed class FollowMetrics
{
    public const string MeterName = "TvTracker.Tracking";

    private readonly Counter<long> started;

    public FollowMetrics(IMeterFactory meters) =>
        started = meters.Create(MeterName).CreateCounter<long>("follows.started", description: "Shows newly followed, by where the follow started");

    /// <param name="from">What the request said of where it started; anything unknown counts as direct.</param>
    public void Started(string? from) => started.Add(1, new KeyValuePair<string, object?>("source", FollowSources.Of(from)));
}

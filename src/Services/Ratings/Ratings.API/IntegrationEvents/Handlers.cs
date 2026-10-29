using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Ratings.API.IntegrationEvents;

/// <summary>Records watched episodes so comments can stay hidden until the reader has watched, and for episode duels.</summary>
public sealed class EpisodeWatchedHandler(RatingsContext db, TimeProvider time) : IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>
{
    public async Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(EpisodeWatchedHandler), ct))
        {
            return;
        }

        var ids = e.EpisodeIds.Distinct().ToList();
        var existing = await db.WatchedEpisodes
            .Where(w => w.UserId == e.UserId && ids.Contains(w.EpisodeId))
            .ToDictionaryAsync(w => w.EpisodeId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var id in ids)
        {
            if (existing.TryGetValue(id, out var copy))
            {
                copy.ShowId = e.ShowId;
            }
            else
            {
                db.WatchedEpisodes.Add(new WatchedEpisodeCopy { UserId = e.UserId, EpisodeId = id, ShowId = e.ShowId, RecordedAtUtc = now });
            }
        }
        db.MarkProcessed(e, nameof(EpisodeWatchedHandler));
        await db.SaveChangesAsync(ct);
    }
}

public sealed class EpisodeUnwatchedHandler(RatingsContext db) : IIntegrationEventHandler<EpisodeUnwatchedIntegrationEvent>
{
    public async Task HandleAsync(EpisodeUnwatchedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(EpisodeUnwatchedHandler), ct))
        {
            return;
        }

        await db.WatchedEpisodes.Where(w => w.UserId == e.UserId && w.EpisodeId == e.EpisodeId).ExecuteDeleteAsync(ct);
        db.MarkProcessed(e, nameof(EpisodeUnwatchedHandler));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Repairs the watched copy from Tracking's daily snapshot: adds what's missing, fills in show ids, and removes what's
/// gone, keeping anything recorded after the snapshot was read. Before this, an unwatch never reached Ratings.
/// </summary>
public sealed class TrackingSnapshotHandler(RatingsContext db, TimeProvider time) : IIntegrationEventHandler<TrackingSnapshotIntegrationEvent>
{
    public async Task HandleAsync(TrackingSnapshotIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(TrackingSnapshotHandler), ct))
        {
            return;
        }

        var watches = e.Watches.DistinctBy(w => w.EpisodeId).ToDictionary(w => w.EpisodeId, w => w.ShowId);
        var copies = await db.WatchedEpisodes.Where(w => w.UserId == e.UserId).ToListAsync(ct);
        var takenAt = e.TakenAtUtc.UtcDateTime;
        foreach (var copy in copies)
        {
            if (watches.TryGetValue(copy.EpisodeId, out var showId))
            {
                copy.ShowId = showId;
            }
            else if (copy.RecordedAtUtc < takenAt)
            {
                db.WatchedEpisodes.Remove(copy);
            }
        }
        var known = copies.Select(c => c.EpisodeId).ToHashSet();
        var now = time.GetUtcNow().UtcDateTime;
        db.WatchedEpisodes.AddRange(watches.Where(w => !known.Contains(w.Key))
            .Select(w => new WatchedEpisodeCopy { UserId = e.UserId, EpisodeId = w.Key, ShowId = w.Value, RecordedAtUtc = now }));
        db.MarkProcessed(e, nameof(TrackingSnapshotHandler));
        await db.SaveChangesAsync(ct);
    }
}

public sealed class UserDeletedHandler(RatingsContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.ShowRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.EpisodeRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        var comments = db.Comments.Where(c => c.UserId == e.UserId).Select(c => c.Id);
        await db.Flags.Where(f => f.TargetKind == "comment" && comments.Contains(f.TargetId)).ExecuteDeleteAsync(ct);
        await db.Comments.Where(c => c.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Reactions.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.WatchedEpisodes.Where(w => w.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Duels.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);

        // Their community insights go with the votes and flags on them; their own votes and flags go too.
        var insights = db.Insights.Where(i => i.UserId == e.UserId).Select(i => i.Id);
        await db.InsightVotes.Where(v => insights.Contains(v.InsightId) || v.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Flags.Where(f => f.TargetKind == "insight" && insights.Contains(f.TargetId) || f.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Insights.Where(i => i.UserId == e.UserId).ExecuteDeleteAsync(ct);
        // Corrections already applied stay in the catalog; the suggestions themselves are theirs and go.
        await db.Corrections.Where(c => c.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;
using Ratings.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Ratings.API.IntegrationEvents;

/// <summary>
/// Records watched episodes so comments can stay hidden until the reader has watched, and for episode duels.
/// </summary>
public sealed class EpisodeWatchedHandler(RatingsContext db) : IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>
{
    public async Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct)
    {
        await db.HandleOnceAsync(e, nameof(EpisodeWatchedHandler), async () =>
        {
            var ids = e.EpisodeIds.Distinct().ToList();
            var existing = await db.WatchedEpisodes
                .Where(w => w.UserId == e.UserId && ids.Contains(w.EpisodeId))
                .ToDictionaryAsync(w => w.EpisodeId, ct);
            foreach (var id in ids)
            {
                if (existing.TryGetValue(id, out var copy))
                {
                    copy.ShowId = e.ShowId;
                    copy.RecordedAtUtc = SnapshotRepair.Recorded(copy.RecordedAtUtc, e.CreatedAt);
                }
                else
                {
                    db.WatchedEpisodes.Add(new WatchedEpisodeCopy { UserId = e.UserId, EpisodeId = id, ShowId = e.ShowId, RecordedAtUtc = e.CreatedAt.UtcDateTime });
                }
            }
        }, ct);
    }
}

public sealed class EpisodeUnwatchedHandler(RatingsContext db) : IIntegrationEventHandler<EpisodeUnwatchedIntegrationEvent>
{
    // A favourite-character vote on the episode goes with the watch (see RatingsContext).
    public Task HandleAsync(EpisodeUnwatchedIntegrationEvent e, CancellationToken ct) =>
        db.HandleOnceAsync(e, nameof(EpisodeUnwatchedHandler),
            () => db.WatchedEpisodes.Where(w => w.UserId == e.UserId && w.EpisodeId == e.EpisodeId).ExecuteDeleteAsync(ct), ct);
}

/// <summary>
/// Repairs the watched copy from Tracking's daily snapshot: adds what's missing, fills in show ids, and removes what's
/// gone, keeping anything recorded after the snapshot was read. Before this, an unwatch never reached Ratings.
/// </summary>
public sealed class TrackingSnapshotHandler(RatingsContext db) : IIntegrationEventHandler<TrackingSnapshotIntegrationEvent>
{
    public async Task HandleAsync(TrackingSnapshotIntegrationEvent e, CancellationToken ct)
    {
        await db.HandleOnceAsync(e, nameof(TrackingSnapshotHandler), async () =>
        {
            var watches = SnapshotRepair.Of(await db.WatchedEpisodes.Where(w => w.UserId == e.UserId).ToListAsync(ct), e.Watches,
                w => w.EpisodeId, w => w.EpisodeId, w => w.RecordedAtUtc, e.TakenAtUtc);
            foreach (var (copy, watch) in watches.Both)
            {
                copy.ShowId = watch.ShowId;
            }
            db.WatchedEpisodes.RemoveRange(watches.Gone);
            db.WatchedEpisodes.AddRange(watches.Missing.Select(w => new WatchedEpisodeCopy
            {
                UserId = e.UserId, EpisodeId = w.EpisodeId, ShowId = w.ShowId, RecordedAtUtc = e.TakenAtUtc.UtcDateTime
            }));
        }, ct);
    }
}

public sealed class UserDeletedHandler(RatingsContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    internal const string FormerModerator = "a former moderator";

    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        // What they did as a moderator stays, in the log and on the corrections they reviewed, without their name (as
        // Research keeps a former moderator's notes): the corrections first, while the log still says which were theirs.
        var reviewed = db.ModerationActions.Where(a => a.ModeratorId == e.UserId && a.TargetKind == Apis.CommunityApi.CorrectionKind).Select(a => a.TargetId);
        await db.Corrections.Where(c => reviewed.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ReviewedBy, FormerModerator), ct);
        await db.ModerationActions.Where(a => a.ModeratorId == e.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ModeratorId, "").SetProperty(a => a.ModeratorName, FormerModerator), ct);

        await db.ShowRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.EpisodeRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        var comments = db.Comments.Where(c => c.UserId == e.UserId).Select(c => c.Id);
        await db.Flags.Where(f => f.TargetKind == "comment" && comments.Contains(f.TargetId)).ExecuteDeleteAsync(ct);
        await db.Comments.Where(c => c.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Reactions.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.CharacterVotes.Where(v => v.UserId == e.UserId).ExecuteDeleteAsync(ct);
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

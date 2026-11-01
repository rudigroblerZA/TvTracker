using Microsoft.EntityFrameworkCore;
using Recommendations.API.Data;
using Recommendations.API.Services;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Recommendations.API.IntegrationEvents;

/// <summary>Keeps show features current from the catalog, and show DNA from Analytics.</summary>
public sealed class ShowFeaturesHandler(RecommendationsContext db, TimeProvider time) :
    IIntegrationEventHandler<ShowAddedIntegrationEvent>,
    IIntegrationEventHandler<ShowUpdatedIntegrationEvent>,
    IIntegrationEventHandler<ShowDnaChangedIntegrationEvent>,
    IIntegrationEventHandler<ShowNanogenresChangedIntegrationEvent>
{
    public async Task HandleAsync(ShowNanogenresChangedIntegrationEvent e, CancellationToken ct)
    {
        var handler = $"{nameof(ShowFeaturesHandler)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var shows = e.Shows.DistinctBy(s => s.ShowId).ToList();
        var ids = shows.Select(s => s.ShowId).ToList();
        var existing = await db.Nanogenres.Where(n => ids.Contains(n.ShowId)).ToDictionaryAsync(n => n.ShowId, ct);
        var changed = false;
        foreach (var show in shows)
        {
            if (!existing.TryGetValue(show.ShowId, out var row))
            {
                row = new ShowNanogenres { ShowId = show.ShowId };
                db.Nanogenres.Add(row);
            }
            else if (row.Names.SequenceEqual(show.Nanogenres))
            {
                continue;
            }
            row.Names = show.Nanogenres.ToList();
            changed = true;
        }

        if (changed)
        {
            await MarkEveryoneDirtyAsync(ct);
        }

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Each show's traits replace the ones held; a show sent without traits has none any more.</summary>
    public async Task HandleAsync(ShowDnaChangedIntegrationEvent e, CancellationToken ct)
    {
        var handler = $"{nameof(ShowFeaturesHandler)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var shows = e.Shows.DistinctBy(s => s.ShowId).ToList();
        var ids = shows.Select(s => s.ShowId).ToList();
        var existing = await db.Dna.Where(d => ids.Contains(d.ShowId)).ToDictionaryAsync(d => d.ShowId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var changed = false;
        foreach (var show in shows)
        {
            if (!existing.TryGetValue(show.ShowId, out var dna))
            {
                dna = new ShowDna { ShowId = show.ShowId };
                db.Dna.Add(dna);
            }
            else if (dna.Traits.Count == show.Traits.Count && show.Traits.All(t => dna.Traits.TryGetValue(t.Key, out var v) && v == t.Value))
            {
                continue;
            }

            dna.Traits = new Dictionary<string, double>(show.Traits);
            dna.UpdatedAtUtc = now;
            changed = true;
        }

        if (changed)
        {
            await MarkEveryoneDirtyAsync(ct);
        }

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }

    public Task HandleAsync(ShowAddedIntegrationEvent e, CancellationToken ct) =>
        UpsertAsync(e, e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, e.Keywords, e.CastNames, e.SimilarShowIds, e.Popularity, ct);

    public Task HandleAsync(ShowUpdatedIntegrationEvent e, CancellationToken ct) =>
        UpsertAsync(e, e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, e.Keywords, e.CastNames, e.SimilarShowIds, e.Popularity, ct);

    /// <summary>Any user's picks can depend on any show's DNA or nanogenres: rebuild every list that isn't already queued.</summary>
    private async Task MarkEveryoneDirtyAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var queued = db.DirtyUsers.Select(d => d.UserId);
        var users = await db.Signals.Select(s => s.UserId).Distinct().Where(u => !queued.Contains(u)).ToListAsync(ct);
        db.DirtyUsers.AddRange(users.Select(u => new DirtyUser { UserId = u, MarkedAtUtc = now }));
    }

    private async Task UpsertAsync(
        IntegrationEvent e, Guid showId, string title, string? posterUrl, ShowStatus status,
        IReadOnlyList<string> genres, IReadOnlyList<string> keywords, IReadOnlyList<string> cast,
        IReadOnlyList<Guid> similar, double popularity, CancellationToken ct)
    {
        var handler = $"{nameof(ShowFeaturesHandler)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var show = await db.Shows.FindAsync([showId], ct);
        if (show is null)
        {
            show = new ShowFeatures { ShowId = showId, Title = title };
            db.Shows.Add(show);
        }

        show.Title = title;
        show.PosterUrl = posterUrl;
        show.Status = status;
        show.Genres = genres.ToList();
        show.Keywords = keywords.ToList();
        show.CastNames = cast.ToList();
        show.SimilarShowIds = similar.ToList();
        show.Popularity = popularity;
        show.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Updates a user's signals from follows, watches and ratings, and flags their list for a rebuild.</summary>
public sealed class UserSignalHandler(RecommendationsContext db, RecommendationStore store, TimeProvider time) :
    IIntegrationEventHandler<ShowFollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowUnfollowedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeUnwatchedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatingRemovedIntegrationEvent>
{
    public Task HandleAsync(ShowFollowedIntegrationEvent e, CancellationToken ct) =>
        UpdateAsync(e, e.UserId, e.ShowId, s => s.Followed = true, ct);

    public Task HandleAsync(ShowUnfollowedIntegrationEvent e, CancellationToken ct) =>
        UpdateAsync(e, e.UserId, e.ShowId, s => s.Followed = false, ct);

    public Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct) =>
        UpdateAsync(e, e.UserId, e.ShowId, s => s.EpisodesWatched += e.EpisodeIds.Count, ct);

    public Task HandleAsync(EpisodeUnwatchedIntegrationEvent e, CancellationToken ct) =>
        UpdateAsync(e, e.UserId, e.ShowId, s => s.EpisodesWatched = Math.Max(0, s.EpisodesWatched - 1), ct);

    public Task HandleAsync(ShowRatedIntegrationEvent e, CancellationToken ct) =>
        UpdateAsync(e, e.UserId, e.ShowId, s => s.Rating = e.Stars, ct);

    public Task HandleAsync(ShowRatingRemovedIntegrationEvent e, CancellationToken ct) =>
        UpdateAsync(e, e.UserId, e.ShowId, s => s.Rating = null, ct);

    private async Task UpdateAsync(IntegrationEvent e, string userId, Guid showId, Action<UserShowSignal> apply, CancellationToken ct)
    {
        var handler = $"{nameof(UserSignalHandler)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var signal = await db.Signals.FindAsync([userId, showId], ct);
        if (signal is null)
        {
            signal = new UserShowSignal { UserId = userId, ShowId = showId };
            db.Signals.Add(signal);
        }

        apply(signal);
        signal.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        await store.MarkDirtyAsync(userId, ct);
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Repairs a user's signals from Tracking's and Ratings' daily snapshots (follows, episodes watched per show, ratings).
/// A signal updated after the snapshot was taken is left alone.
/// </summary>
public sealed class SnapshotHandler(RecommendationsContext db, RecommendationStore store, TimeProvider time) :
    IIntegrationEventHandler<TrackingSnapshotIntegrationEvent>,
    IIntegrationEventHandler<RatingsSnapshotIntegrationEvent>
{
    public Task HandleAsync(TrackingSnapshotIntegrationEvent e, CancellationToken ct)
    {
        var followed = e.Follows.Select(f => f.ShowId).ToHashSet();
        var watched = e.Watches.DistinctBy(w => w.EpisodeId).GroupBy(w => w.ShowId).ToDictionary(g => g.Key, g => g.Count());
        return RepairAsync(e, e.UserId, e.TakenAtUtc, followed.Union(watched.Keys), signal =>
        {
            signal.Followed = followed.Contains(signal.ShowId);
            signal.EpisodesWatched = watched.GetValueOrDefault(signal.ShowId);
        }, ct);
    }

    public Task HandleAsync(RatingsSnapshotIntegrationEvent e, CancellationToken ct)
    {
        var ratings = e.Shows.DistinctBy(r => r.ShowId).ToDictionary(r => r.ShowId, r => r.Stars);
        return RepairAsync(e, e.UserId, e.TakenAtUtc, ratings.Keys, signal => signal.Rating = ratings.TryGetValue(signal.ShowId, out var stars) ? stars : null, ct);
    }

    /// <param name="shows">Shows the snapshot has something for; the user's other signals are repaired too.</param>
    private async Task RepairAsync(IntegrationEvent e, string userId, DateTimeOffset takenAt, IEnumerable<Guid> shows,
        Action<UserShowSignal> apply, CancellationToken ct)
    {
        var handler = $"{nameof(SnapshotHandler)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var signals = await db.Signals.Where(s => s.UserId == userId).ToDictionaryAsync(s => s.ShowId, ct);
        foreach (var showId in shows.Where(id => !signals.ContainsKey(id)))
        {
            var added = new UserShowSignal { UserId = userId, ShowId = showId };
            db.Signals.Add(added);
            signals[showId] = added;
        }

        var changed = false;
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var signal in signals.Values.Where(s => s.UpdatedAtUtc < takenAt.UtcDateTime))
        {
            var before = (signal.Followed, signal.EpisodesWatched, signal.Rating);
            apply(signal);
            if (before != (signal.Followed, signal.EpisodesWatched, signal.Rating))
            {
                signal.UpdatedAtUtc = now;
                changed = true;
            }
        }

        if (changed)
        {
            await store.MarkDirtyAsync(userId, ct);
        }
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class UserDeletedHandler(RecommendationsContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.Signals.Where(s => s.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Dismissals.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.UserRecommendations.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.DirtyUsers.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Plans.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Keeps <see cref="UserPlanCopy"/> from Identity's plan changes and daily profile snapshot (which heals a lost change), as
/// Tracking does; the newest by Identity's change time wins.
/// </summary>
public sealed class PlanHandlers(RecommendationsContext db)
    : IIntegrationEventHandler<UserPlanChangedIntegrationEvent>, IIntegrationEventHandler<UserProfileSnapshotIntegrationEvent>
{
    public Task HandleAsync(UserPlanChangedIntegrationEvent e, CancellationToken ct) =>
        ApplyAsync(e, e.UserId, e.Plan, e.ChangedAtUtc, ct);

    // A snapshot from before plans existed carries none; a plan never changed is free since sign-up, as old as can be.
    public Task HandleAsync(UserProfileSnapshotIntegrationEvent e, CancellationToken ct) =>
        e.Plan is { } plan ? ApplyAsync(e, e.UserId, plan, e.PlanChangedAtUtc ?? DateTimeOffset.MinValue, ct) : Task.CompletedTask;

    private async Task ApplyAsync(IntegrationEvent e, string userId, string plan, DateTimeOffset changedAt, CancellationToken ct)
    {
        var handler = $"{nameof(PlanHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var copy = await db.Plans.FindAsync([userId], ct);
        if (copy is null)
        {
            // Free with no change is what no row already means.
            if (plan != TvTracker.IntegrationEvents.Plans.Free || changedAt != DateTimeOffset.MinValue)
            {
                db.Plans.Add(new UserPlanCopy { UserId = userId, Plan = plan, ChangedAtUtc = changedAt });
            }
        }
        else if (changedAt >= copy.ChangedAtUtc)
        {
            copy.Plan = plan;
            copy.ChangedAtUtc = changedAt;
        }

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

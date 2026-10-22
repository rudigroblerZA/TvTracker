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

public sealed class UserDeletedHandler(RecommendationsContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.Signals.Where(s => s.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Dismissals.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.UserRecommendations.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.DirtyUsers.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}

using Analytics.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Analytics.Worker.IntegrationEvents;

/// <summary>Keeps Analytics' copy of shows, episodes, announced seasons and cast current from the catalog.</summary>
public sealed class CatalogEventHandlers(AnalyticsContext db, TimeProvider time) :
    IIntegrationEventHandler<ShowAddedIntegrationEvent>,
    IIntegrationEventHandler<ShowUpdatedIntegrationEvent>,
    IIntegrationEventHandler<PeopleCareersUpdatedIntegrationEvent>,
    IIntegrationEventHandler<ShowStatusChangedIntegrationEvent>,
    IIntegrationEventHandler<EpisodesScheduledIntegrationEvent>,
    IIntegrationEventHandler<SeasonAnnouncedIntegrationEvent>
{
    public Task HandleAsync(ShowAddedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, e.Popularity, e.ExternalRating, e.Cast, e.Creatives, ct));

    public Task HandleAsync(ShowUpdatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, e.Popularity, e.ExternalRating, e.Cast, e.Creatives, ct));

    public Task HandleAsync(PeopleCareersUpdatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var ids = e.People.Select(p => p.PersonId).Distinct().ToList();
            var existing = await db.Careers.Where(c => ids.Contains(c.PersonId)).ToDictionaryAsync(c => c.PersonId, ct);
            foreach (var person in e.People.DistinctBy(p => p.PersonId))
            {
                if (!existing.TryGetValue(person.PersonId, out var career))
                {
                    career = new PersonCareerCopy { PersonId = person.PersonId, Name = person.Name };
                    db.Careers.Add(career);
                }

                career.Name = person.Name;
                career.Shows = person.Shows;
                career.Created = person.Created;
                career.ExecutiveProduced = person.ExecutiveProduced;
            }
        });

    public Task HandleAsync(ShowStatusChangedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            if (await db.Shows.FindAsync([e.ShowId], ct) is { } show)
            {
                show.Status = e.NewStatus;
                show.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            }
        });

    public Task HandleAsync(EpisodesScheduledIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            // The episodes may arrive before their show (for example after a missed ShowAdded).
            if (await db.Shows.FindAsync([e.ShowId], ct) is null)
            {
                db.Shows.Add(new ShowCopy { ShowId = e.ShowId, Title = e.ShowTitle, UpdatedAtUtc = time.GetUtcNow().UtcDateTime });
            }

            var ids = e.Episodes.Select(x => x.EpisodeId).ToList();
            var existing = await db.Episodes.Where(x => ids.Contains(x.EpisodeId)).ToDictionaryAsync(x => x.EpisodeId, ct);

            foreach (var scheduled in e.Episodes)
            {
                if (!existing.TryGetValue(scheduled.EpisodeId, out var episode))
                {
                    episode = new EpisodeCopy { EpisodeId = scheduled.EpisodeId, ShowId = e.ShowId, Title = scheduled.Title };
                    db.Episodes.Add(episode);
                    existing[scheduled.EpisodeId] = episode;
                }

                episode.SeasonNumber = scheduled.SeasonNumber;
                episode.EpisodeNumber = scheduled.EpisodeNumber;
                episode.Title = scheduled.Title;
                episode.AirsAtUtc = scheduled.AirsAtUtc;
                episode.RuntimeMinutes = scheduled.RuntimeMinutes;
                episode.ExternalRating = scheduled.ExternalRating;
            }
        });

    public Task HandleAsync(SeasonAnnouncedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var season = await db.AnnouncedSeasons.FindAsync([e.ShowId, e.SeasonNumber], ct);
            if (season is null)
            {
                db.AnnouncedSeasons.Add(new AnnouncedSeason { ShowId = e.ShowId, SeasonNumber = e.SeasonNumber, PremieresAtUtc = e.PremieresAtUtc });
            }
            else
            {
                season.PremieresAtUtc = e.PremieresAtUtc;
            }
        });

    private async Task UpsertShowAsync(
        Guid showId, string title, string? posterUrl, ShowStatus status, IReadOnlyList<string> genres, double popularity,
        double? externalRating, IReadOnlyList<CastCredit>? cast, IReadOnlyList<CreativeCredit>? creatives, CancellationToken ct)
    {
        var show = await db.Shows.FindAsync([showId], ct);
        if (show is null)
        {
            show = new ShowCopy { ShowId = showId, Title = title };
            db.Shows.Add(show);
        }

        show.Title = title;
        show.PosterUrl = posterUrl;
        show.Status = status;
        show.Genres = genres.ToList();
        show.Popularity = popularity;
        show.ExternalRating = externalRating;
        show.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        // Likewise for creatives: null in older events.
        if (creatives is not null)
        {
            await db.Creatives.Where(c => c.ShowId == showId).ExecuteDeleteAsync(ct);
            db.Creatives.AddRange(creatives
                .DistinctBy(c => c.PersonId)
                .Select((c, order) => new CreativeCopy { ShowId = showId, PersonId = c.PersonId, Name = c.Name, Roles = c.Roles.ToList(), Order = order }));
            show.CreativesKnown = true;
        }

        // Events published before the cast was added carry none; keep what we have rather than clearing it.
        if (cast is not null)
        {
            await db.Cast.Where(c => c.ShowId == showId).ExecuteDeleteAsync(ct);
            db.Cast.AddRange(cast
                .DistinctBy(c => c.Name)
                .Select((c, order) => new CastCopy
                {
                    ShowId = showId,
                    PersonName = c.Name,
                    PersonId = c.PersonId,
                    Character = c.Character,
                    CharacterExternalIds = c.CharacterExternalIds.ToList(),
                    Order = order
                }));
        }
    }

    private async Task HandleOnceAsync(IntegrationEvent e, CancellationToken ct, Func<Task> apply)
    {
        var handler = $"{nameof(CatalogEventHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        await apply();
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Keeps each user's latest rating of shows and episodes. Only aggregates ever leave this service.</summary>
public sealed class RatingEventHandlers(AnalyticsContext db) :
    IIntegrationEventHandler<ShowRatedIntegrationEvent>,
    IIntegrationEventHandler<ShowRatingRemovedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeRatedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeRatingRemovedIntegrationEvent>
{
    public Task HandleAsync(ShowRatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var rating = await db.ShowRatings.FindAsync([e.UserId, e.ShowId], ct);
            if (rating is null)
            {
                db.ShowRatings.Add(new UserShowRating { UserId = e.UserId, ShowId = e.ShowId, Stars = e.Stars });
            }
            else
            {
                rating.Stars = e.Stars;
            }
        });

    public Task HandleAsync(ShowRatingRemovedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            if (await db.ShowRatings.FindAsync([e.UserId, e.ShowId], ct) is { } rating)
            {
                db.ShowRatings.Remove(rating);
            }
        });

    public Task HandleAsync(EpisodeRatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var rating = await db.EpisodeRatings.FindAsync([e.UserId, e.EpisodeId], ct);
            if (rating is null)
            {
                db.EpisodeRatings.Add(new UserEpisodeRating { UserId = e.UserId, EpisodeId = e.EpisodeId, ShowId = e.ShowId, Stars = e.Stars });
            }
            else
            {
                rating.Stars = e.Stars;
            }
        });

    public Task HandleAsync(EpisodeRatingRemovedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            if (await db.EpisodeRatings.FindAsync([e.UserId, e.EpisodeId], ct) is { } rating)
            {
                db.EpisodeRatings.Remove(rating);
            }
        });

    private async Task HandleOnceAsync(IntegrationEvent e, CancellationToken ct, Func<Task> apply)
    {
        var handler = $"{nameof(RatingEventHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        await apply();
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Keeps each user's watched episodes and follows. Only aggregates ever leave this service.</summary>
public sealed class TrackingEventHandlers(AnalyticsContext db) :
    IIntegrationEventHandler<EpisodeWatchedIntegrationEvent>,
    IIntegrationEventHandler<EpisodeUnwatchedIntegrationEvent>,
    IIntegrationEventHandler<ShowFollowedIntegrationEvent>,
    IIntegrationEventHandler<ShowUnfollowedIntegrationEvent>
{
    public Task HandleAsync(EpisodeWatchedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var ids = e.EpisodeIds.Distinct().ToList();
            var existing = await db.Watches
                .Where(w => w.UserId == e.UserId && ids.Contains(w.EpisodeId))
                .Select(w => w.EpisodeId)
                .ToListAsync(ct);

            db.Watches.AddRange(ids.Except(existing).Select(id => new UserEpisodeWatch
            {
                UserId = e.UserId,
                EpisodeId = id,
                ShowId = e.ShowId,
                WatchedAtUtc = (e.WatchedAtUtc ?? e.CreatedAt).UtcDateTime
            }));
        });

    public Task HandleAsync(EpisodeUnwatchedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            if (await db.Watches.FindAsync([e.UserId, e.EpisodeId], ct) is { } watch)
            {
                db.Watches.Remove(watch);
            }
        });

    public Task HandleAsync(ShowFollowedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            if (await db.Follows.FindAsync([e.UserId, e.ShowId], ct) is null)
            {
                db.Follows.Add(new UserFollow { UserId = e.UserId, ShowId = e.ShowId, FollowedAtUtc = e.CreatedAt.UtcDateTime });
            }
        });

    public Task HandleAsync(ShowUnfollowedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            if (await db.Follows.FindAsync([e.UserId, e.ShowId], ct) is { } follow)
            {
                db.Follows.Remove(follow);
            }
        });

    private async Task HandleOnceAsync(IntegrationEvent e, CancellationToken ct, Func<Task> apply)
    {
        var handler = $"{nameof(TrackingEventHandlers)}.{e.GetType().Name}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        await apply();
        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Deletes everything a deleted user left here; the next hourly run drops them from every aggregate.</summary>
public sealed class UserDeletedHandler(AnalyticsContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        await db.ShowRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.EpisodeRatings.Where(r => r.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Watches.Where(w => w.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Follows.Where(f => f.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}

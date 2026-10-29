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
    IIntegrationEventHandler<PeopleCastCreditsUpdatedIntegrationEvent>,
    IIntegrationEventHandler<ShowStatusChangedIntegrationEvent>,
    IIntegrationEventHandler<EpisodesScheduledIntegrationEvent>,
    IIntegrationEventHandler<SeasonAnnouncedIntegrationEvent>
{
    public Task HandleAsync(ShowAddedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, e.Popularity, e.ExternalRating, e.Cast, e.Creatives, e.NetworkKnown ? e.Network : null, e.NetworkKnown, ct));

    public Task HandleAsync(ShowUpdatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, () => UpsertShowAsync(e.ShowId, e.Title, e.PosterUrl, e.Status, e.Genres, e.Popularity, e.ExternalRating, e.Cast, e.Creatives, e.NetworkKnown ? e.Network : null, e.NetworkKnown, ct));

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

    /// <summary>Each person's roles replace the ones held for them.</summary>
    public Task HandleAsync(PeopleCastCreditsUpdatedIntegrationEvent e, CancellationToken ct) =>
        HandleOnceAsync(e, ct, async () =>
        {
            var people = e.People.DistinctBy(p => p.PersonId).ToList();
            var ids = people.Select(p => p.PersonId).ToList();
            var existing = (await db.CastCredits.Where(c => ids.Contains(c.PersonId)).ToListAsync(ct)).ToLookup(c => c.PersonId);
            foreach (var person in people)
            {
                var credits = existing[person.PersonId].ToDictionary(c => c.ShowExternalId);
                foreach (var role in person.Roles.DistinctBy(r => r.ShowExternalId))
                {
                    if (!credits.Remove(role.ShowExternalId, out var credit))
                    {
                        credit = new PersonCastCreditCopy { PersonId = person.PersonId, ShowExternalId = role.ShowExternalId, Title = role.Title };
                        db.CastCredits.Add(credit);
                    }

                    credit.ShowId = role.ShowId;
                    credit.Title = role.Title;
                    credit.PosterUrl = role.PosterUrl;
                    credit.Status = role.Status;
                    credit.Premiered = role.Premiered;
                    credit.Popularity = role.Popularity;
                    credit.Rating = role.Rating;
                    credit.Character = role.Character;
                    credit.Voice = role.Voice;
                }

                // What's left was dropped from the person's credits.
                db.CastCredits.RemoveRange(credits.Values);
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
        double? externalRating, IReadOnlyList<CastCredit>? cast, IReadOnlyList<CreativeCredit>? creatives, string? network, bool networkKnown,
        CancellationToken ct)
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

        // Events from before the network was added don't carry it; keep what we have.
        if (networkKnown)
        {
            show.Network = network;
            show.NetworkKnown = true;
        }

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

/// <summary>
/// Repairs a user's watches, follows and ratings from Tracking's and Ratings' daily snapshots: adds what's missing and
/// removes what's gone. A row recorded after the snapshot was taken is kept, since the snapshot can't know about it.
/// Ratings have no time here, so a rating changed in the seconds between a snapshot and its delivery can be undone
/// until the next day's snapshot; that's the price of healing lost events without a dead-letter queue.
/// </summary>
public sealed class SnapshotHandlers(AnalyticsContext db, ILogger<SnapshotHandlers> logger) :
    IIntegrationEventHandler<TrackingSnapshotIntegrationEvent>,
    IIntegrationEventHandler<RatingsSnapshotIntegrationEvent>
{
    public async Task HandleAsync(TrackingSnapshotIntegrationEvent e, CancellationToken ct)
    {
        const string handler = $"{nameof(SnapshotHandlers)}.{nameof(TrackingSnapshotIntegrationEvent)}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var takenAt = e.TakenAtUtc.UtcDateTime;
        var watches = await db.Watches.Where(w => w.UserId == e.UserId).ToListAsync(ct);
        var watched = watches.Select(w => w.EpisodeId).ToHashSet();
        var snapshotWatches = e.Watches.DistinctBy(w => w.EpisodeId).ToDictionary(w => w.EpisodeId);
        var addWatches = snapshotWatches.Values.Where(w => !watched.Contains(w.EpisodeId))
            .Select(w => new UserEpisodeWatch { UserId = e.UserId, EpisodeId = w.EpisodeId, ShowId = w.ShowId, WatchedAtUtc = w.WatchedAtUtc.UtcDateTime })
            .ToList();
        var removeWatches = watches.Where(w => !snapshotWatches.ContainsKey(w.EpisodeId) && w.WatchedAtUtc < takenAt).ToList();
        db.Watches.AddRange(addWatches);
        db.Watches.RemoveRange(removeWatches);

        var follows = await db.Follows.Where(f => f.UserId == e.UserId).ToListAsync(ct);
        var followed = follows.Select(f => f.ShowId).ToHashSet();
        var snapshotFollows = e.Follows.DistinctBy(f => f.ShowId).ToDictionary(f => f.ShowId);
        var addFollows = snapshotFollows.Values.Where(f => !followed.Contains(f.ShowId))
            .Select(f => new UserFollow { UserId = e.UserId, ShowId = f.ShowId, FollowedAtUtc = f.FollowedAtUtc.UtcDateTime })
            .ToList();
        var removeFollows = follows.Where(f => !snapshotFollows.ContainsKey(f.ShowId) && f.FollowedAtUtc < takenAt).ToList();
        db.Follows.AddRange(addFollows);
        db.Follows.RemoveRange(removeFollows);

        if (addWatches.Count + removeWatches.Count + addFollows.Count + removeFollows.Count > 0)
        {
            logger.LogInformation(
                "Repaired {UserId}'s activity from Tracking's snapshot: watches +{AddedWatches} -{RemovedWatches}, follows +{AddedFollows} -{RemovedFollows}",
                e.UserId, addWatches.Count, removeWatches.Count, addFollows.Count, removeFollows.Count);
        }

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(RatingsSnapshotIntegrationEvent e, CancellationToken ct)
    {
        const string handler = $"{nameof(SnapshotHandlers)}.{nameof(RatingsSnapshotIntegrationEvent)}";
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var changes = 0;
        var shows = await db.ShowRatings.Where(r => r.UserId == e.UserId).ToDictionaryAsync(r => r.ShowId, ct);
        foreach (var rating in e.Shows.DistinctBy(r => r.ShowId))
        {
            if (!shows.Remove(rating.ShowId, out var existing))
            {
                db.ShowRatings.Add(new UserShowRating { UserId = e.UserId, ShowId = rating.ShowId, Stars = rating.Stars });
                changes++;
            }
            else if (existing.Stars != rating.Stars)
            {
                existing.Stars = rating.Stars;
                changes++;
            }
        }
        db.ShowRatings.RemoveRange(shows.Values);
        changes += shows.Count;

        var episodes = await db.EpisodeRatings.Where(r => r.UserId == e.UserId).ToDictionaryAsync(r => r.EpisodeId, ct);
        foreach (var rating in e.Episodes.DistinctBy(r => r.EpisodeId))
        {
            if (!episodes.Remove(rating.EpisodeId, out var existing))
            {
                db.EpisodeRatings.Add(new UserEpisodeRating { UserId = e.UserId, EpisodeId = rating.EpisodeId, ShowId = rating.ShowId, Stars = rating.Stars });
                changes++;
            }
            else if (existing.Stars != rating.Stars)
            {
                existing.Stars = rating.Stars;
                changes++;
            }
        }
        db.EpisodeRatings.RemoveRange(episodes.Values);
        changes += episodes.Count;

        if (changes > 0)
        {
            logger.LogInformation("Repaired {UserId}'s ratings from Ratings' snapshot: {Changes} changed", e.UserId, changes);
        }

        db.MarkProcessed(e, handler);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Keeps the copy of Research's episode notes: each event replaces one show's notes, or every show's (the daily send),
/// unless the copy already has a later read of that show. A note deleted after a daily send was read but before it
/// arrived comes back until the next one.
/// </summary>
public sealed class EpisodeNotesHandler(AnalyticsContext db) : IIntegrationEventHandler<EpisodeNotesChangedIntegrationEvent>
{
    public async Task HandleAsync(EpisodeNotesChangedIntegrationEvent e, CancellationToken ct)
    {
        const string handler = nameof(EpisodeNotesHandler);
        if (await db.HasProcessedAsync(e, handler, ct))
        {
            return;
        }

        var existing = await db.EpisodeNotes.Where(n => e.ShowId == null || n.ShowId == e.ShowId).ToListAsync(ct);
        var newer = existing.Where(n => n.TakenAtUtc > e.TakenAtUtc).Select(n => n.ShowId).ToHashSet();
        var incoming = e.Notes.Where(n => !newer.Contains(n.ShowId) && (e.ShowId == null || n.ShowId == e.ShowId))
            .DistinctBy(n => n.EpisodeId).ToDictionary(n => n.EpisodeId);
        foreach (var row in existing.Where(n => !newer.Contains(n.ShowId)))
        {
            if (!incoming.Remove(row.EpisodeId, out var note))
            {
                db.EpisodeNotes.Remove(row);
                continue;
            }
            (row.ShowId, row.Mark, row.Reason, row.SourceUrl, row.TakenAtUtc) = (note.ShowId, note.Mark, note.Reason, note.SourceUrl, e.TakenAtUtc);
        }
        db.EpisodeNotes.AddRange(incoming.Values.Select(n => new EpisodeNoteCopy
        {
            EpisodeId = n.EpisodeId,
            ShowId = n.ShowId,
            Mark = n.Mark,
            Reason = n.Reason,
            SourceUrl = n.SourceUrl,
            TakenAtUtc = e.TakenAtUtc
        }));

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
        await db.Plans.Where(p => p.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.UserServices.Where(s => s.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.SeasonCalls.Where(c => c.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Keeps <see cref="UserPlanCopy"/> from Identity's plan changes and daily profile snapshot (which heals a lost change),
/// for the plan's limits on Analytics.API's endpoints. The newest by Identity's change time wins.
/// </summary>
public sealed class PlanHandlers(AnalyticsContext db)
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

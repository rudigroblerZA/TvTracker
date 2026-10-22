using Catalog.Data;
using Catalog.SyncWorker.Providers;
using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Catalog.SyncWorker;

/// <summary>
/// Fetches every catalog actor's roles across the provider, for the reunion radar (Analytics finds where two actors of a
/// show someone loves work together again). One call per person, <see cref="PeoplePerRound"/> people a minute so the
/// provider's rate limit stays shared, leads of popular shows first; refreshed monthly. Each round's people are sent
/// as one <see cref="TvTracker.IntegrationEvents.PeopleCastCreditsUpdatedIntegrationEvent"/>.
/// </summary>
public sealed class CastCreditsService(
    IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<CastCreditsService> logger) : BackgroundService
{
    private const int PeoplePerRound = 30;
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromHours(1);

    // People the provider failed, and when to try them again. Only this service's loop touches it.
    private readonly Dictionary<Guid, DateTimeOffset> _failed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RefreshAsync(stoppingToken) is > 0 and var people)
                {
                    logger.LogInformation("Cast credits: refreshed {People} people", people);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Refreshing cast credits failed; retrying in {Round}", Round);
            }

            await Task.Delay(Round, time, stoppingToken);
        }
    }

    private async Task<int> RefreshAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
        var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
        var now = time.GetUtcNow();
        var staleBefore = now.UtcDateTime - MaxAge;
        var skip = _failed.Where(f => f.Value > now).Select(f => f.Key).ToList();

        // Never fetched first, then the oldest; among those, leads before supporting cast, popular shows first.
        var due = await db.People
            .Where(p => p.ExternalId != null && (p.CastCreditsRefreshedAtUtc == null || p.CastCreditsRefreshedAtUtc < staleBefore))
            .Where(p => db.Cast.Any(c => c.PersonId == p.Id) && !skip.Contains(p.Id))
            .OrderBy(p => p.CastCreditsRefreshedAtUtc != null)
            .ThenBy(p => p.CastCreditsRefreshedAtUtc)
            .ThenBy(p => db.Cast.Where(c => c.PersonId == p.Id).Min(c => c.Order))
            .ThenByDescending(p => db.Shows.Where(s => db.Cast.Any(c => c.ShowId == s.Id && c.PersonId == p.Id)).Max(s => s.Popularity))
            .Take(PeoplePerRound)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var refreshed = new List<(Person Person, List<ProviderActingRole> Roles)>();
        foreach (var person in due)
        {
            try
            {
                refreshed.Add((person, (await provider.GetCastCreditsAsync(person.ExternalId!, ct))?.ToList() ?? []));
            }
            catch (HttpRequestException ex)
            {
                // One person failing shouldn't stop the round, or come first in every round after.
                _failed[person.Id] = now + FailureRetry;
                logger.LogWarning(ex, "Couldn't fetch cast credits for {Person}; retrying in {Retry}", person.ExternalId, FailureRetry);
            }
        }

        var externalIds = refreshed.SelectMany(r => r.Roles).Select(r => r.ShowExternalId).Distinct().ToList();
        var catalogIds = await db.Shows
            .Where(s => s.Source == provider.Name && externalIds.Contains(s.ExternalId))
            .ToDictionaryAsync(s => s.ExternalId, s => s.Id, ct);
        var personIds = refreshed.Select(r => r.Person.Id).ToList();
        var existing = (await db.CastCredits.Where(c => personIds.Contains(c.PersonId)).ToListAsync(ct)).ToLookup(c => c.PersonId);

        var sent = new List<(Person, IEnumerable<PersonCastCredit>)>();
        foreach (var (person, roles) in refreshed)
        {
            var credits = existing[person.Id].ToDictionary(c => c.ShowExternalId);
            foreach (var role in roles)
            {
                if (!credits.TryGetValue(role.ShowExternalId, out var credit))
                {
                    credit = new PersonCastCredit { PersonId = person.Id, ShowExternalId = role.ShowExternalId, Title = role.Title };
                    credits[role.ShowExternalId] = credit;
                    db.CastCredits.Add(credit);
                }

                credit.ShowId = catalogIds.TryGetValue(role.ShowExternalId, out var showId) ? showId : null;
                credit.Title = role.Title;
                credit.PosterUrl = role.PosterUrl;
                credit.Status = role.Status;
                credit.Premiered = role.Premiered;
                credit.Popularity = role.Popularity;
                credit.Rating = role.Rating;
                credit.Character = role.Character;
                credit.Voice = role.Voice;
            }

            var gone = credits.Values.Where(c => roles.All(r => r.ShowExternalId != c.ShowExternalId)).ToList();
            db.CastCredits.RemoveRange(gone);
            person.CastCreditsRefreshedAtUtc = now.UtcDateTime;
            sent.Add((person, credits.Values.Except(gone).ToList()));
        }

        outbox.Add(CatalogSynchronizer.CastCreditsEvent(sent));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return refreshed.Count;
    }
}

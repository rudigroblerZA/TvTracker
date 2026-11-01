using Catalog.Data;
using Catalog.SyncWorker.Providers;
using Microsoft.EntityFrameworkCore;

namespace Catalog.SyncWorker.Namesakes;

/// <summary>
/// Same name: for each catalog show, the provider's other shows with the same title (remakes, revivals, a namesake from
/// another decade), from one title search per show. The catalog's own casts and provider credits only reach an older
/// version when one of its actors is in a catalog show; the search finds Shōgun's 1980 miniseries too.
/// <list type="bullet">
/// <item><b>Same title, normalised</b> (<see cref="NamesakeTitles.Key"/>): case, punctuation, "&amp;" and a trailing
/// "(2024)" or "(US)" don't count, so "The Office (US)" is The Office; anything else does ("Doctor Who: The Movie" isn't).</item>
/// <item><b>Gently:</b> <see cref="ShowsPerRound"/> searches a minute through the provider's shared limiter, most popular
/// shows first, each again monthly; a show's rows are replaced with what the search says then. A failed search leaves
/// that show for the next round.</item>
/// <item><b>The show's own country</b> comes from its own search result, kept on the lookup: the catalog doesn't store
/// a network's country, and "another country" is what tells a remake abroad.</item>
/// </list>
/// </summary>
public sealed class NamesakesService(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<NamesakesService> logger) : BackgroundService
{
    private const int ShowsPerRound = 20;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Idle = TimeSpan.FromHours(6);
    private static readonly TimeSpan LookupAfter = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstDelay, time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Idle;
            try
            {
                if (await LookUpAsync(stoppingToken) > 0)
                {
                    next = Round;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Same name: looking up namesakes failed; trying again in {Round}", Round);
                next = Round;
            }
            await Task.Delay(next, time, stoppingToken);
        }
    }

    private async Task<int> LookUpAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var provider = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
        var now = time.GetUtcNow().UtcDateTime;
        var staleBefore = now - LookupAfter;
        var due = await db.Shows.AsNoTracking()
            .Where(s => s.Source == provider.Name && !db.NamesakeLookups.Any(l => l.ShowId == s.Id && l.CheckedAtUtc >= staleBefore))
            .OrderByDescending(s => s.Popularity)
            .Select(s => new { s.Id, s.ExternalId, s.Title })
            .Take(ShowsPerRound)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var found = new List<(Guid ShowId, string? Country, List<ProviderShowSummary> Namesakes)>();
        foreach (var show in due)
        {
            try
            {
                var key = NamesakeTitles.Key(show.Title);
                var results = await provider.SearchShowsAsync(show.Title, ct);
                var self = results.FirstOrDefault(r => r.ExternalId == show.ExternalId);
                var namesakes = results
                    .Where(r => r.ExternalId != show.ExternalId && NamesakeTitles.Key(r.Title) == key)
                    .DistinctBy(r => r.ExternalId)
                    .ToList();
                found.Add((show.Id, self?.Country, namesakes));
            }
            catch (HttpRequestException ex)
            {
                // One search failing shouldn't stop the round; the show stays due.
                logger.LogDebug(ex, "Same name: searching for {Title} failed", show.Title);
            }
        }

        var ids = found.Select(f => f.ShowId).ToList();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Namesakes.Where(n => ids.Contains(n.ShowId)).ExecuteDeleteAsync(ct);
            await db.NamesakeLookups.Where(l => ids.Contains(l.ShowId)).ExecuteDeleteAsync(ct);
            foreach (var (showId, country, namesakes) in found)
            {
                db.Namesakes.AddRange(namesakes.Select(n => new ShowNamesake
                {
                    ShowId = showId, ExternalId = n.ExternalId, Title = n.Title, PosterUrl = n.PosterUrl, Premiered = n.Premiered, Ended = n.Ended,
                    Network = n.Network, Country = n.Country, Language = n.Language, Kind = n.Kind, Popularity = n.Popularity, Rating = n.Rating
                }));
                db.NamesakeLookups.Add(new ShowNamesakeLookup { ShowId = showId, CheckedAtUtc = now, Country = country });
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        logger.LogInformation("Same name: looked up {Shows} shows, {WithNamesakes} with a namesake", found.Count, found.Count(f => f.Namesakes.Count > 0));
        return due.Count;
    }
}

/// <summary>How two titles are compared for Same name.</summary>
public static partial class NamesakeTitles
{
    /// <summary>Lower case, "&amp;" read as "and", a trailing parenthesis ("(2024)", "(US)") and punctuation dropped.</summary>
    public static string Key(string title)
    {
        var text = TrailingParenthesis().Replace(title, "").Replace("&", " and ").ToLowerInvariant();
        return string.Join(' ', NotWord().Replace(text, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial System.Text.RegularExpressions.Regex TrailingParenthesis();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial System.Text.RegularExpressions.Regex NotWord();
}

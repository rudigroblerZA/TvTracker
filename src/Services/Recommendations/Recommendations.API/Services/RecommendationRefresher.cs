using Microsoft.EntityFrameworkCore;
using Recommendations.API.Data;

namespace Recommendations.API.Services;

/// <summary>Rebuilds "for you" lists for users whose signals changed, so requests only read pre-computed rows.</summary>
public sealed class RecommendationRefresher(IServiceScopeFactory scopeFactory, ILogger<RecommendationRefresher> logger) : BackgroundService
{
    public const int ListSize = 30;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RecommendationsContext>();
                var store = scope.ServiceProvider.GetRequiredService<RecommendationStore>();

                var dirty = await db.DirtyUsers.OrderBy(d => d.MarkedAtUtc).Take(50).ToListAsync(stoppingToken);
                foreach (var user in dirty)
                {
                    await store.RebuildAsync(user.UserId, stoppingToken);
                    // Only clear the flag if it wasn't set again while we were computing.
                    await db.DirtyUsers
                        .Where(d => d.UserId == user.UserId && d.MarkedAtUtc <= user.MarkedAtUtc)
                        .ExecuteDeleteAsync(stoppingToken);
                }

                if (dirty.Count > 0)
                {
                    logger.LogInformation("Rebuilt recommendations for {Count} users", dirty.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Recommendation refresh failed");
            }
        }
    }
}

/// <summary>Reads, rebuilds and invalidates pre-computed recommendation lists.</summary>
public sealed class RecommendationStore(RecommendationsContext db, RecommendationEngine engine, TimeProvider time)
{
    public async Task<List<UserRecommendation>> GetOrBuildAsync(string userId, CancellationToken ct)
    {
        var existing = await db.UserRecommendations.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Rank)
            .ToListAsync(ct);

        return existing.Count > 0 ? existing : await RebuildAsync(userId, ct);
    }

    public async Task<List<UserRecommendation>> RebuildAsync(string userId, CancellationToken ct)
    {
        var picks = await engine.ComputeForUserAsync(userId, RecommendationRefresher.ListSize, ct);
        var now = time.GetUtcNow().UtcDateTime;

        await db.UserRecommendations.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);

        var rows = picks.Select((p, i) => new UserRecommendation
        {
            UserId = userId,
            ShowId = p.ShowId,
            Rank = i,
            Score = p.Score,
            Reason = p.Reason,
            ComputedAtUtc = now
        }).ToList();

        db.UserRecommendations.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows;
    }

    /// <summary>Flags the user for a background rebuild. Call before SaveChanges.</summary>
    public async Task MarkDirtyAsync(string userId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var dirty = await db.DirtyUsers.FindAsync([userId], ct);
        if (dirty is null)
        {
            db.DirtyUsers.Add(new DirtyUser { UserId = userId, MarkedAtUtc = now });
        }
        else
        {
            dirty.MarkedAtUtc = now;
        }
    }
}

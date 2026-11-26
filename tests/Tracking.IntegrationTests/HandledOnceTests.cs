using Microsoft.EntityFrameworkCore;
using Tracking.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Tracking.IntegrationTests;

/// <summary>
/// The inbox every service's handlers go through (<see cref="InboxExtensions.HandleOnceAsync"/>), against Postgres with
/// Tracking's database standing in for any service's: an event is applied once per handler, and its change and its
/// mark are saved together or not at all.
/// </summary>
public sealed class HandledOnceTests(TrackingDatabase database) : IClassFixture<TrackingDatabase>
{
    private const string Handler = "A handler";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string userId = $"user-{Guid.NewGuid():N}";

    private Task<List<string>> PlansAsync(TrackingContext db) =>
        db.Plans.AsNoTracking().Where(p => p.UserId.StartsWith(userId)).OrderBy(p => p.UserId).Select(p => p.UserId).ToListAsync(Ct);

    private UserPlanCopy Plan(string suffix = "") => new() { UserId = userId + suffix, Plan = Plans.Plus, ChangedAtUtc = DateTimeOffset.UtcNow };

    [Fact]
    public async Task An_event_delivered_again_is_applied_once()
    {
        var e = new UserDeletedIntegrationEvent(userId);
        var applied = 0;
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var db = database.Context();
            await db.HandleOnceAsync(e, Handler, () =>
            {
                applied++;
                db.Plans.Add(Plan($"-{applied}"));
                return Task.CompletedTask;
            }, Ct);
        }

        await using var read = database.Context();
        Assert.Equal(1, applied);
        Assert.Equal([$"{userId}-1"], await PlansAsync(read));
    }

    [Fact]
    public async Task Another_handler_of_the_same_event_still_applies_it()
    {
        var e = new UserDeletedIntegrationEvent(userId);
        var applied = new List<string>();
        foreach (var handler in new[] { Handler, "Another handler" })
        {
            await using var db = database.Context();
            await db.HandleOnceAsync(e, handler, () =>
            {
                applied.Add(handler);
                return Task.CompletedTask;
            }, Ct);
        }

        Assert.Equal([Handler, "Another handler"], applied);
    }

    [Fact]
    public async Task A_failure_after_a_bulk_delete_leaves_nothing_changed_and_the_event_unmarked()
    {
        await using (var seed = database.Context())
        {
            seed.Plans.Add(Plan());
            await seed.SaveChangesAsync(Ct);
        }
        var e = new UserDeletedIntegrationEvent(userId);

        await using (var db = database.Context())
        {
            // The delete runs at once; the handler then fails before its change is saved.
            await Assert.ThrowsAnyAsync<Exception>(() => db.HandleOnceAsync(e, Handler, async () =>
            {
                await db.Plans.Where(p => p.UserId == userId).ExecuteDeleteAsync(Ct);
                throw new InvalidOperationException("the handler failed part way");
            }, Ct));
        }

        await using (var read = database.Context())
        {
            Assert.Equal([userId], await PlansAsync(read));
            Assert.False(await read.HasProcessedAsync(e, Handler, Ct));
        }

        // The bus delivers it again, and this time it goes through.
        await using (var db = database.Context())
        {
            await db.HandleOnceAsync(e, Handler, async () =>
            {
                await db.Plans.Where(p => p.UserId == userId).ExecuteDeleteAsync(Ct);
                db.Plans.Add(Plan("-new"));
            }, Ct);
        }

        await using (var read = database.Context())
        {
            Assert.Equal([$"{userId}-new"], await PlansAsync(read));
            Assert.True(await read.HasProcessedAsync(e, Handler, Ct));
        }
    }

    [Fact]
    public async Task What_was_tracked_before_the_call_is_not_saved_with_it()
    {
        await using var db = database.Context();
        db.Plans.Add(Plan("-stray"));

        await db.HandleOnceAsync(new UserDeletedIntegrationEvent(userId), Handler, () => Task.CompletedTask, Ct);

        Assert.Empty(await PlansAsync(db));
    }
}

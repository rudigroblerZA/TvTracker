using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tracking.API.Services;
using Tracking.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;
using TvTracker.Tests;

namespace Tracking.IntegrationTests;

/// <summary>
/// Follows and watched episodes through <see cref="TrackingService"/>: what is stored, which status a show ends up
/// in, and what is told to the other services.
/// </summary>
public sealed class TrackingServiceTests(TrackingDatabase database) : IClassFixture<TrackingDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider time = new(Now);
    private readonly RecordingEventBus bus = new();
    private readonly string userId = $"user-{Guid.NewGuid():N}";
    private readonly Guid showId = Guid.NewGuid();

    private TrackingService Service(TrackingContext db)
    {
        var services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        var outbox = new IntegrationEventOutbox<TrackingContext>(db, bus, NullLogger<IntegrationEventOutbox<TrackingContext>>.Instance);
        return new TrackingService(db, outbox, new UserCache(services.GetRequiredService<HybridCache>()), time);
    }

    private async Task<List<EpisodeCopy>> SeedShowAsync(ShowStatus status, int aired, int unaired = 0)
    {
        await using var db = database.Context();
        db.Shows.Add(new ShowCopy { ShowId = showId, Title = "Show", Status = status });
        var episodes = Enumerable.Range(1, aired + unaired).Select(n => new EpisodeCopy
        {
            EpisodeId = Guid.NewGuid(),
            ShowId = showId,
            SeasonNumber = 1,
            EpisodeNumber = n,
            Title = $"Episode {n}",
            AirsAtUtc = n <= aired ? Now.AddDays(-30 + n) : Now.AddDays(n)
        }).ToList();
        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync(Ct);
        return episodes;
    }

    private async Task<Follow?> FollowOfAsync()
    {
        await using var db = database.Context();
        return await db.Follows.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == userId && f.ShowId == showId, Ct);
    }

    // --- Following ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Following_a_show_stores_it_and_tells_the_other_services()
    {
        await using (var db = database.Context())
        {
            await Service(db).FollowAsync(userId, showId, WatchStatus.PlanToWatch, Ct);
        }

        var follow = await FollowOfAsync();
        Assert.Equal((WatchStatus.PlanToWatch, Now.UtcDateTime), (follow!.Status, follow.FollowedAtUtc));
        Assert.Equal(showId, Assert.Single(bus.Of<ShowFollowedIntegrationEvent>(), e => e.UserId == userId).ShowId);
    }

    [Fact]
    public async Task Following_again_changes_the_status_but_does_not_announce_a_second_follow()
    {
        await using (var db = database.Context())
        {
            await Service(db).FollowAsync(userId, showId, WatchStatus.PlanToWatch, Ct);
        }
        bus.Clear();
        time.Advance(TimeSpan.FromHours(1));

        await using (var db = database.Context())
        {
            await Service(db).FollowAsync(userId, showId, WatchStatus.Watching, Ct);
        }

        var follow = await FollowOfAsync();
        Assert.Equal(WatchStatus.Watching, follow!.Status);
        Assert.Equal(Now.UtcDateTime.AddHours(1), follow.UpdatedAtUtc);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task Unfollowing_removes_the_follow_once_and_a_second_time_finds_nothing()
    {
        await using (var db = database.Context())
        {
            await Service(db).FollowAsync(userId, showId, WatchStatus.Watching, Ct);
        }
        bus.Clear();

        await using (var db = database.Context())
        {
            Assert.True(await Service(db).UnfollowAsync(userId, showId, Ct));
        }
        await using (var db = database.Context())
        {
            Assert.False(await Service(db).UnfollowAsync(userId, showId, Ct));
        }

        Assert.Null(await FollowOfAsync());
        Assert.Single(bus.Of<ShowUnfollowedIntegrationEvent>());
    }

    [Fact]
    public async Task Setting_the_status_of_a_show_not_followed_does_nothing()
    {
        await using var db = database.Context();

        Assert.Null(await Service(db).SetStatusAsync(userId, showId, WatchStatus.Completed, Ct));
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task Completing_a_show_by_hand_announces_it_only_the_first_time()
    {
        await using (var db = database.Context())
        {
            await Service(db).FollowAsync(userId, showId, WatchStatus.Watching, Ct);
        }
        bus.Clear();

        foreach (var status in new[] { WatchStatus.Completed, WatchStatus.Completed })
        {
            await using var db = database.Context();
            await Service(db).SetStatusAsync(userId, showId, status, Ct);
        }

        Assert.Single(bus.Of<ShowCompletedIntegrationEvent>());
    }

    // --- Watching ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Watching_an_episode_of_a_show_not_followed_follows_it()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 3);

        await using (var db = database.Context())
        {
            var added = await Service(db).MarkWatchedAsync(userId, [episodes[0]], null, Ct);
            Assert.Equal([episodes[0].EpisodeId], added);
        }

        Assert.Equal(WatchStatus.Watching, (await FollowOfAsync())!.Status);
        Assert.Single(bus.Of<ShowFollowedIntegrationEvent>());
        Assert.Equal([episodes[0].EpisodeId], Assert.Single(bus.Of<EpisodeWatchedIntegrationEvent>()).EpisodeIds);
    }

    [Fact]
    public async Task Marking_the_same_episode_twice_adds_it_once()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 3);
        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, [episodes[0]], null, Ct);
        }
        bus.Clear();

        await using (var db = database.Context())
        {
            Assert.Empty(await Service(db).MarkWatchedAsync(userId, [episodes[0]], null, Ct));
        }

        Assert.Empty(bus.Published);
        await using var check = database.Context();
        Assert.Equal(1, await check.WatchedEpisodes.CountAsync(w => w.UserId == userId, Ct));
    }

    [Fact]
    public async Task A_watch_time_in_the_future_is_taken_as_now()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 2);

        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, [episodes[0]], Now.AddDays(5), Ct);
        }

        await using var check = database.Context();
        Assert.Equal(Now.UtcDateTime, (await check.WatchedEpisodes.SingleAsync(w => w.UserId == userId, Ct)).WatchedAtUtc);
    }

    [Fact]
    public async Task Watching_moves_a_paused_or_planned_show_to_watching()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 3);
        await using (var db = database.Context())
        {
            await Service(db).FollowAsync(userId, showId, WatchStatus.Paused, Ct);
        }

        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, [episodes[0]], null, Ct);
        }

        Assert.Equal(WatchStatus.Watching, (await FollowOfAsync())!.Status);
    }

    [Fact]
    public async Task Watching_every_aired_episode_of_an_ended_show_completes_it()
    {
        var episodes = await SeedShowAsync(ShowStatus.Ended, aired: 2);

        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, episodes, null, Ct);
        }

        Assert.Equal(WatchStatus.Completed, (await FollowOfAsync())!.Status);
        Assert.Single(bus.Of<ShowCompletedIntegrationEvent>());
    }

    [Fact]
    public async Task A_show_still_running_is_not_completed_by_watching_everything_so_far()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 2);

        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, episodes, null, Ct);
        }

        Assert.Equal(WatchStatus.Watching, (await FollowOfAsync())!.Status);
        Assert.Empty(bus.Of<ShowCompletedIntegrationEvent>());
    }

    [Fact]
    public async Task An_ended_show_with_an_unwatched_aired_episode_is_not_completed()
    {
        var episodes = await SeedShowAsync(ShowStatus.Ended, aired: 3);

        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, episodes.Take(2).ToList(), null, Ct);
        }

        Assert.Equal(WatchStatus.Watching, (await FollowOfAsync())!.Status);
    }

    [Fact]
    public async Task Unwatching_an_episode_takes_a_completed_show_back_to_watching()
    {
        var episodes = await SeedShowAsync(ShowStatus.Ended, aired: 2);
        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, episodes, null, Ct);
        }
        bus.Clear();

        await using (var db = database.Context())
        {
            Assert.True(await Service(db).UnmarkWatchedAsync(userId, episodes[1].EpisodeId, null, Ct));
        }

        Assert.Equal(WatchStatus.Watching, (await FollowOfAsync())!.Status);
        Assert.Equal(episodes[1].EpisodeId, Assert.Single(bus.Of<EpisodeUnwatchedIntegrationEvent>()).EpisodeId);
    }

    [Fact]
    public async Task A_replayed_unwatch_older_than_the_watch_is_ignored()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 2);
        await using (var db = database.Context())
        {
            await Service(db).MarkWatchedAsync(userId, [episodes[0]], null, Ct);
        }

        await using (var db = database.Context())
        {
            Assert.False(await Service(db).UnmarkWatchedAsync(userId, episodes[0].EpisodeId, Now.AddHours(-1), Ct));
        }

        await using var check = database.Context();
        Assert.True(await check.WatchedEpisodes.AnyAsync(w => w.UserId == userId && w.EpisodeId == episodes[0].EpisodeId, Ct));
    }

    // --- Importing a history -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_imported_history_keeps_each_watch_time_and_follows_from_the_earliest()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 3);
        var first = Now.AddYears(-2);
        var second = Now.AddYears(-1);

        await using (var db = database.Context())
        {
            var added = await Service(db).ImportWatchesAsync(userId,
                [(episodes[0].EpisodeId, second), (episodes[0].EpisodeId, first), (episodes[1].EpisodeId, second)], Ct);
            Assert.Equal(2, added);
        }

        await using var check = database.Context();
        var watched = await check.WatchedEpisodes.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.EpisodeId, w => w.WatchedAtUtc, Ct);
        Assert.Equal(first.UtcDateTime, watched[episodes[0].EpisodeId]);
        Assert.Equal(second.UtcDateTime, watched[episodes[1].EpisodeId]);
        Assert.Equal(first.UtcDateTime, (await FollowOfAsync())!.FollowedAtUtc);
        Assert.Equal(2, bus.Of<EpisodeWatchedIntegrationEvent>().Count());
    }

    [Fact]
    public async Task Importing_the_same_history_again_adds_nothing_and_unknown_episodes_are_skipped()
    {
        var episodes = await SeedShowAsync(ShowStatus.Airing, aired: 2);
        var watches = new List<(Guid, DateTimeOffset)> { (episodes[0].EpisodeId, Now.AddYears(-1)), (Guid.NewGuid(), Now.AddYears(-1)) };
        await using (var db = database.Context())
        {
            Assert.Equal(1, await Service(db).ImportWatchesAsync(userId, watches, Ct));
        }

        await using (var db = database.Context())
        {
            Assert.Equal(0, await Service(db).ImportWatchesAsync(userId, watches, Ct));
        }
    }
}

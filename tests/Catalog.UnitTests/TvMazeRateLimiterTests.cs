using Catalog.SyncWorker.Providers;
using Microsoft.Extensions.Time.Testing;

namespace Catalog.UnitTests;

/// <summary>
/// TVmaze's budget: 18 calls in any 10 seconds, interactive calls first, background calls capped at 12 (see CONTEXT.md,
/// TVmaze budget).
/// </summary>
public class TvMazeRateLimiterTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Background_stops_at_its_share_and_leaves_room_for_people()
    {
        var limiter = new TvMazeRateLimiter(time);

        var background = Acquire(limiter, TvMazeLane.Background, 15);

        Assert.Equal(TvMazeRateLimiter.BackgroundLimit, background.Count(t => t.IsCompletedSuccessfully));
        // Someone turning up still gets a call at once.
        Assert.True(limiter.AcquireAsync(TvMazeLane.Interactive, default).IsCompletedSuccessfully);
        Assert.Equal((0, 3), limiter.Waiting);
    }

    [Fact]
    public void Interactive_calls_use_the_whole_window()
    {
        var limiter = new TvMazeRateLimiter(time);

        var calls = Acquire(limiter, TvMazeLane.Interactive, 20);

        Assert.Equal(TvMazeRateLimiter.PermitLimit, calls.Count(t => t.IsCompletedSuccessfully));
        time.Advance(TvMazeRateLimiter.Window);
        Assert.All(calls, t => Assert.True(t.IsCompletedSuccessfully));
    }

    [Fact]
    public void A_waiting_person_goes_before_a_queue_of_background_calls()
    {
        var limiter = new TvMazeRateLimiter(time);
        Acquire(limiter, TvMazeLane.Interactive, TvMazeRateLimiter.PermitLimit);

        var backfill = Acquire(limiter, TvMazeLane.Background, 13);
        time.Advance(TimeSpan.FromSeconds(1));
        var search = limiter.AcquireAsync(TvMazeLane.Interactive, default);
        Assert.False(search.IsCompleted);

        time.Advance(TvMazeRateLimiter.Window);

        // The search queued last, but goes first; the backfill gets the background share of what's left.
        Assert.True(search.IsCompletedSuccessfully);
        Assert.Equal(TvMazeRateLimiter.BackgroundLimit - 1, backfill.Count(t => t.IsCompletedSuccessfully));
    }

    [Fact]
    public void Background_never_overtakes_a_waiting_person()
    {
        var limiter = new TvMazeRateLimiter(time);
        Acquire(limiter, TvMazeLane.Interactive, TvMazeRateLimiter.PermitLimit);
        var person = limiter.AcquireAsync(TvMazeLane.Interactive, default);

        time.Advance(TvMazeRateLimiter.Window);
        var job = limiter.AcquireAsync(TvMazeLane.Background, default);

        Assert.True(person.IsCompletedSuccessfully);
        Assert.True(job.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_cancelled_wait_gives_its_place_up()
    {
        var limiter = new TvMazeRateLimiter(time);
        Acquire(limiter, TvMazeLane.Background, TvMazeRateLimiter.BackgroundLimit);
        using var cancel = new CancellationTokenSource();
        var gaveUp = limiter.AcquireAsync(TvMazeLane.Background, cancel.Token);
        var next = limiter.AcquireAsync(TvMazeLane.Background, default);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        Assert.Equal((0, 1), limiter.Waiting);

        time.Advance(TvMazeRateLimiter.Window);
        Assert.True(next.IsCompletedSuccessfully);
    }

    [Fact]
    public void The_window_slides()
    {
        var limiter = new TvMazeRateLimiter(time);
        Acquire(limiter, TvMazeLane.Interactive, 9);
        time.Advance(TimeSpan.FromSeconds(5));
        Acquire(limiter, TvMazeLane.Interactive, 9);
        var waiting = limiter.AcquireAsync(TvMazeLane.Interactive, default);

        // Five seconds later the first nine have left the window; the second nine haven't.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(waiting.IsCompletedSuccessfully);
        Assert.Equal(8, Acquire(limiter, TvMazeLane.Interactive, 9).Count(t => t.IsCompletedSuccessfully));
    }

    [Fact]
    public void Calls_belong_to_the_scope_they_were_made_in()
    {
        Assert.Equal(("other", TvMazeLane.Background), TvMazeCaller.Current);
        using (TvMazeCaller.Begin("search-import", TvMazeLane.Interactive))
        {
            Assert.Equal(("search-import", TvMazeLane.Interactive), TvMazeCaller.Current);
            using (TvMazeCaller.Begin("refresh", TvMazeLane.Background))
            {
                Assert.Equal("refresh", TvMazeCaller.Current.Name);
            }
            Assert.Equal("search-import", TvMazeCaller.Current.Name);
        }
        Assert.Equal("other", TvMazeCaller.Current.Name);
    }

    private static List<Task> Acquire(TvMazeRateLimiter limiter, TvMazeLane lane, int count) =>
        Enumerable.Range(0, count).Select(_ => limiter.AcquireAsync(lane, default)).ToList();
}

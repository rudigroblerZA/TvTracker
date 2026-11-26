using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ratings.API.Apis;
using Ratings.API.IntegrationEvents;
using Ratings.API.Services;
using Ratings.Data;
using TvTracker.IntegrationEvents;

namespace Ratings.IntegrationTests;

/// <summary>
/// Favourite-character votes as they run on Postgres: the vote is one upsert, the counts and the reader's own pick
/// come from one grouped query, a vote needs a watch and goes with it, and the show it counts towards is the one the
/// watch names.
/// </summary>
public sealed class CharacterVotesTests(RatingsDatabase database) : IClassFixture<RatingsDatabase>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid show = Guid.NewGuid();
    private readonly Guid episode = Guid.NewGuid();
    private readonly Guid athena = Guid.NewGuid();
    private readonly Guid hen = Guid.NewGuid();
    private readonly Guid buck = Guid.NewGuid();

    private static string NewUser() => $"user-{Guid.NewGuid():N}";

    private static ClaimsPrincipal As(string userId) => new(new ClaimsIdentity([new Claim("sub", userId)], "test"));

    private readonly List<ServiceProvider> caches = [];

    public void Dispose() => caches.ForEach(cache => cache.Dispose());

    /// <summary>
    /// The catalog, as far as votes ask it: it has the one show, whose cast is the three people above unless
    /// <paramref name="cast"/> says otherwise, and the one episode unless <paramref name="knowsEpisode"/> is false.
    /// Anything else it is asked for isn't there. Each has its own cache, so one catalog's answers don't reach the next.
    /// </summary>
    private CatalogLookup Catalog(HttpStatusCode status = HttpStatusCode.OK, Guid[]? cast = null, bool knowsEpisode = true)
    {
        var people = cast ?? [athena, hen, buck];
        var (theShow, theEpisode) = ($"/api/v1/shows/{show}", $"/api/v1/episodes/{episode}");
        var handler = new Stub(path => path switch
        {
            _ when status != HttpStatusCode.OK => new HttpResponseMessage(status),
            _ when path == theShow => Json(new { cast = people.Select(id => new { personId = id }) }),
            _ when path == theEpisode && knowsEpisode => Json(new { id = episode, showId = show }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        caches.Add(cache);
        return new CatalogLookup(new HttpClient(handler) { BaseAddress = new Uri("http://catalog/") },
            cache.GetRequiredService<HybridCache>(), NullLogger<CatalogLookup>.Instance);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class Stub(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private async Task<string> WatcherAsync(Guid? ofEpisode = null, Guid? inShow = null, bool showKnown = true)
    {
        var user = NewUser();
        await WatchAsync(user, ofEpisode ?? episode, showKnown ? inShow ?? show : null);
        return user;
    }

    private async Task WatchAsync(string user, Guid ofEpisode, Guid? inShow)
    {
        await using var db = database.Context();
        db.WatchedEpisodes.Add(new WatchedEpisodeCopy { UserId = user, EpisodeId = ofEpisode, ShowId = inShow, RecordedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<object> VoteAsync(string user, Guid person, Guid? ofEpisode = null, CatalogLookup? catalog = null)
    {
        await using var db = database.Context();
        var result = await CharacterVotesApi.VoteAsync(ofEpisode ?? episode, new CharacterVoteRequest(person), As(user), db, catalog ?? Catalog(), Ct);
        return result.Result;
    }

    private async Task<EpisodeCharacterVotesDto> VotesAsync(string user, Guid? ofEpisode = null)
    {
        await using var db = database.Context();
        return (await CharacterVotesApi.GetEpisodeVotesAsync(ofEpisode ?? episode, As(user), db, Ct)).Value!;
    }

    [Fact]
    public async Task A_vote_is_counted_and_comes_back_as_the_voters_own()
    {
        var (me, other) = (await WatcherAsync(), await WatcherAsync());

        var mine = Assert.IsType<Ok<EpisodeCharacterVotesDto>>(await VoteAsync(me, athena)).Value!;
        await VoteAsync(other, hen);
        await VoteAsync(await WatcherAsync(), hen);

        Assert.Equal((false, 1, athena), (mine.Locked, mine.Total, mine.Mine!.Value));
        var seen = await VotesAsync(me);
        Assert.Equal(3, seen.Total);
        // Most votes first.
        Assert.Equal([(hen, 2), (athena, 1)], seen.Counts.Select(c => (c.PersonId, c.Votes)));
        Assert.Equal(athena, seen.Mine);
        Assert.Equal(hen, (await VotesAsync(other)).Mine);
    }

    [Fact]
    public async Task Voting_again_changes_the_vote_and_does_not_add_one()
    {
        var me = await WatcherAsync();

        await VoteAsync(me, athena);
        var changed = Assert.IsType<Ok<EpisodeCharacterVotesDto>>(await VoteAsync(me, buck)).Value!;

        Assert.Equal(buck, changed.Mine);
        await using var db = database.Context();
        Assert.Equal(buck, Assert.Single(await db.CharacterVotes.Where(v => v.UserId == me).ToListAsync(Ct)).PersonId);
    }

    [Fact]
    public async Task Someone_who_has_not_watched_the_episode_sees_nothing_and_cannot_vote()
    {
        var (me, watcher) = (NewUser(), await WatcherAsync());
        await VoteAsync(watcher, athena);

        var locked = await VotesAsync(me);

        Assert.Equal((true, 0), (locked.Locked, locked.Total));
        Assert.Empty(locked.Counts);
        Assert.IsType<Conflict<string>>(await VoteAsync(me, athena));
    }

    [Fact]
    public async Task A_vote_counts_towards_the_show_the_watch_names()
    {
        var me = await WatcherAsync();

        await VoteAsync(me, athena);

        await using var db = database.Context();
        Assert.Equal(show, (await db.CharacterVotes.SingleAsync(v => v.UserId == me, Ct)).ShowId);
    }

    [Fact]
    public async Task A_watch_that_does_not_name_its_show_takes_it_from_the_catalog()
    {
        var me = await WatcherAsync(showKnown: false);

        await VoteAsync(me, athena);

        await using var db = database.Context();
        Assert.Equal(show, (await db.CharacterVotes.SingleAsync(v => v.UserId == me, Ct)).ShowId);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_watch_that_does_not_name_its_show_cannot_be_voted_on_when_the_catalog_cannot_name_it_either(HttpStatusCode catalog)
    {
        var me = await WatcherAsync(showKnown: false);

        Assert.IsType<Conflict<string>>(await VoteAsync(me, athena, catalog: Catalog(catalog, knowsEpisode: false)));

        await using var db = database.Context();
        Assert.False(await db.CharacterVotes.AnyAsync(v => v.UserId == me, Ct));
    }

    [Fact]
    public async Task A_show_with_nobody_in_its_cast_takes_no_votes()
    {
        var me = await WatcherAsync();

        Assert.IsType<ValidationProblem>(await VoteAsync(me, athena, catalog: Catalog(cast: [])));
    }

    [Fact]
    public async Task A_vote_made_while_the_episode_is_being_unwatched_does_not_outlive_the_watch()
    {
        var me = await WatcherAsync();
        await using var unwatching = database.Context();
        await using var unwatch = await unwatching.Database.BeginTransactionAsync(Ct);
        await unwatching.WatchedEpisodes.Where(w => w.UserId == me).ExecuteDeleteAsync(Ct);

        // The vote has read the watch, and waits on it; the unwatch then goes through.
        var vote = VoteAsync(me, athena);
        while (await unwatching.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from pg_locks where not granted").SingleAsync(Ct) == 0)
        {
            Assert.False(vote.IsCompleted);
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
        await unwatch.CommitAsync(Ct);

        Assert.IsType<Conflict<string>>(await vote);
        await using var db = database.Context();
        Assert.False(await db.CharacterVotes.AnyAsync(v => v.UserId == me, Ct));
    }

    [Fact]
    public async Task Someone_outside_the_shows_cast_cannot_be_voted_for_unless_the_catalog_cannot_say()
    {
        var me = await WatcherAsync();
        var stranger = Guid.NewGuid();

        Assert.IsType<ValidationProblem>(await VoteAsync(me, stranger));
        // The catalog is down: the vote is let through, as it was before there was a check.
        Assert.IsType<Ok<EpisodeCharacterVotesDto>>(await VoteAsync(me, stranger, catalog: Catalog(HttpStatusCode.InternalServerError)));
    }

    [Fact]
    public async Task Taking_a_vote_back_removes_it()
    {
        var me = await WatcherAsync();
        await VoteAsync(me, athena);

        await using var db = database.Context();
        var after = (await CharacterVotesApi.RemoveVoteAsync(episode, As(me), db, Ct)).Value!;

        Assert.Null(after.Mine);
        Assert.DoesNotContain(after.Counts, c => c.PersonId == athena);
        Assert.False(await db.CharacterVotes.AnyAsync(v => v.UserId == me, Ct));
    }

    [Fact]
    public async Task Unwatching_an_episode_takes_the_vote_with_it()
    {
        var me = await WatcherAsync();
        await VoteAsync(me, athena);

        await using (var db = database.Context())
        {
            await new EpisodeUnwatchedHandler(db).HandleAsync(new EpisodeUnwatchedIntegrationEvent(me, show, episode), Ct);
        }

        await using var check = database.Context();
        Assert.False(await check.CharacterVotes.AnyAsync(v => v.UserId == me, Ct));
        Assert.False(await check.WatchedEpisodes.AnyAsync(w => w.UserId == me, Ct));
    }

    private async Task SnapshotAsync(string user, DateTimeOffset takenAt, params Guid[] watched)
    {
        await using var db = database.Context();
        await new TrackingSnapshotHandler(db).HandleAsync(
            new TrackingSnapshotIntegrationEvent(user, takenAt, [], [.. watched.Select(id => new SnapshotWatch(show, id, takenAt))]), Ct);
    }

    [Fact]
    public async Task Watching_again_after_a_snapshot_was_taken_keeps_the_watch_that_snapshot_lacks()
    {
        var me = NewUser();
        var first = DateTimeOffset.UtcNow.AddHours(-3);
        await using (var db = database.Context())
        {
            await new EpisodeWatchedHandler(db).HandleAsync(new EpisodeWatchedIntegrationEvent(me, show, [episode]) { CreatedAt = first }, Ct);
        }
        // The unwatch was lost; they watch it again an hour after Tracking read a snapshot without it, and the watch
        // gets here before the snapshot does.
        await using (var db = database.Context())
        {
            await new EpisodeWatchedHandler(db).HandleAsync(new EpisodeWatchedIntegrationEvent(me, show, [episode]) { CreatedAt = first.AddHours(2) }, Ct);
        }

        await SnapshotAsync(me, first.AddHours(1));

        await using var check = database.Context();
        Assert.True(await check.WatchedEpisodes.AnyAsync(w => w.UserId == me && w.EpisodeId == episode, Ct));
    }

    [Fact]
    public async Task A_snapshot_without_the_watch_takes_the_vote_with_it_and_keeps_the_rest()
    {
        var kept = Guid.NewGuid();
        var me = await WatcherAsync();
        await WatchAsync(me, kept, show);
        await VoteAsync(me, athena);
        await VoteAsync(me, hen, ofEpisode: kept);

        await SnapshotAsync(me, DateTimeOffset.UtcNow.AddMinutes(1), kept);

        await using var check = database.Context();
        Assert.Equal(kept, Assert.Single(await check.WatchedEpisodes.Where(w => w.UserId == me).ToListAsync(Ct)).EpisodeId);
        Assert.Equal(kept, Assert.Single(await check.CharacterVotes.Where(v => v.UserId == me).ToListAsync(Ct)).EpisodeId);
    }

    [Fact]
    public async Task A_snapshot_read_before_the_watch_was_recorded_keeps_the_watch_and_its_vote()
    {
        var me = await WatcherAsync();
        await VoteAsync(me, athena);

        await SnapshotAsync(me, DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var check = database.Context();
        Assert.True(await check.WatchedEpisodes.AnyAsync(w => w.UserId == me, Ct));
        Assert.True(await check.CharacterVotes.AnyAsync(v => v.UserId == me, Ct));
    }

    [Fact]
    public async Task A_shows_favourites_are_its_most_voted_for_cast_across_its_episodes()
    {
        var (second, departed) = (Guid.NewGuid(), Guid.NewGuid());
        await VoteAsync(await WatcherAsync(), athena);
        await VoteAsync(await WatcherAsync(), hen);
        await VoteAsync(await WatcherAsync(second), hen, ofEpisode: second);
        // Someone who has since left the cast: counted in the total, not given a place.
        await VoteAsync(await WatcherAsync(second), departed, ofEpisode: second, catalog: Catalog(HttpStatusCode.InternalServerError));

        await using var db = database.Context();
        var totals = (await CharacterVotesApi.GetShowVotesAsync(show, db, Catalog(), Ct)).Value!;

        Assert.Equal(4, totals.Total);
        Assert.Equal([(hen, 2), (athena, 1)], totals.Top.Select(c => (c.PersonId, c.Votes)));
    }
}

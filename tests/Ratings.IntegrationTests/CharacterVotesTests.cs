using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ratings.API.Apis;
using Ratings.API.Data;
using Ratings.API.IntegrationEvents;
using Ratings.API.Services;
using TvTracker.IntegrationEvents;

namespace Ratings.IntegrationTests;

/// <summary>
/// Favourite-character votes as they run on Postgres: the vote is one upsert, the counts and the reader's own pick
/// come from one grouped query, a vote needs a watch and goes with it, and the show it counts towards is the one the
/// watch names.
/// </summary>
public sealed class CharacterVotesTests(RatingsDatabase database) : IClassFixture<RatingsDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid show = Guid.NewGuid();
    private readonly Guid episode = Guid.NewGuid();
    private readonly Guid athena = Guid.NewGuid();
    private readonly Guid hen = Guid.NewGuid();
    private readonly Guid buck = Guid.NewGuid();

    private static string NewUser() => $"user-{Guid.NewGuid():N}";

    private static ClaimsPrincipal As(string userId) => new(new ClaimsIdentity([new Claim("sub", userId)], "test"));

    /// <summary>
    /// The catalog, as far as votes ask it: the show's cast is the three people above, unless it says otherwise.
    /// </summary>
    private CatalogCast Catalog(HttpStatusCode status = HttpStatusCode.OK, params Guid[] cast)
    {
        var people = cast.Length > 0 ? cast : [athena, hen, buck];
        var handler = new Stub(() => new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(new { cast = people.Select(id => new { personId = id }) })
        });
        return new CatalogCast(new HttpClient(handler) { BaseAddress = new Uri("http://catalog/") },
            new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>(),
            NullLogger<CatalogCast>.Instance);
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    private async Task<string> WatcherAsync(Guid? ofEpisode = null, Guid? inShow = null, bool showKnown = true)
    {
        var user = NewUser();
        await using var db = database.Context();
        db.WatchedEpisodes.Add(new WatchedEpisodeCopy
        {
            UserId = user, EpisodeId = ofEpisode ?? episode, ShowId = showKnown ? inShow ?? show : null, RecordedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<object> VoteAsync(string user, Guid person, Guid? ofEpisode = null, Guid? claimedShow = null, CatalogCast? catalog = null)
    {
        await using var db = database.Context();
        var result = await CharacterVotesApi.VoteAsync(ofEpisode ?? episode, new CharacterVoteRequest(claimedShow ?? show, person), As(user), db,
            catalog ?? Catalog(), Ct);
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
    public async Task A_vote_counts_towards_the_show_the_watch_names_not_the_one_the_request_claims()
    {
        var me = await WatcherAsync();

        await VoteAsync(me, athena, claimedShow: Guid.NewGuid());

        await using var db = database.Context();
        Assert.Equal(show, (await db.CharacterVotes.SingleAsync(v => v.UserId == me, Ct)).ShowId);
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

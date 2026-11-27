using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Analytics.API.Apis;
using Analytics.API.Services;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;
using TvTracker.IntegrationEvents;

namespace Analytics.IntegrationTests;

/// <summary>
/// Taste Match's endpoints against a real database: a link's states, who may do what with it, and what is stored and
/// sent when a friend answers. The catalog and Social are scripted; the score's rules have their own unit tests.
/// </summary>
public sealed class TasteMatchApiTests(AnalyticsDatabase database) : IClassFixture<AnalyticsDatabase>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<Guid> Shows = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();

    private readonly FakeTimeProvider time = new(Now);
    private readonly MatchMetrics metrics = new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    // Each test has its own people, so tests sharing the database never meet.
    private readonly string inviter = $"inviter-{Guid.NewGuid():N}";
    private readonly string member = $"member-{Guid.NewGuid():N}";

    /// <summary>
    /// Whether Social says the two have a block between them.
    /// </summary>
    private bool blocked;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = database.Context();
        if (!await db.Shows.AnyAsync(s => s.ShowId == Shows[0], Ct))
        {
            db.Shows.AddRange(Shows.Select((id, i) => new ShowCopy { ShowId = id, Title = $"Show {i:00}", Genres = [i % 2 == 0 ? "Crime" : "Comedy"] }));
        }
        // The inviter loves the first six; the member the last six of those and two more.
        db.ShowRatings.AddRange(Shows.Take(6).Select(id => new UserShowRating { UserId = inviter, ShowId = id, Stars = 5 }));
        db.ShowRatings.AddRange(Shows.Skip(3).Take(5).Select(id => new UserShowRating { UserId = member, ShowId = id, Stars = 4 }));
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_member_starts_a_match_and_sees_which_of_their_shows_could_be_named()
    {
        var started = await StartAsync();

        Assert.Equal(32, started.Token.Length);
        Assert.Equal(6, started.Nameable.Count);
        var opened = await OpenAsync(started.Token, Guest);
        Assert.Equal(("open", "Ines", null, null), (opened!.State, opened.InviterName, opened.You, opened.Result));
    }

    [Fact]
    public async Task A_member_with_too_few_loved_shows_is_told_so()
    {
        await using var db = database.Context();

        var result = await TasteMatchApi.StartAsync(User($"new-{Guid.NewGuid():N}", "Noor"), db, metrics, time, Ct);

        Assert.Contains("5 more shows", Assert.IsType<ProblemHttpResult>(result.Result).ProblemDetails.Detail);
    }

    [Fact]
    public async Task A_guests_answer_finishes_the_match_once_and_tells_the_inviter()
    {
        var token = (await StartAsync()).Token;

        var answered = await PickAsync(token, Shows.Skip(2).Take(4).ToList(), "  Robin   Smith ");
        var again = await PickAsync(token, Shows.Take(3).ToList(), "Mallory");

        Assert.Equal(("done", "Robin", null), (answered!.State, answered.FriendName, answered.You));
        Assert.NotNull(answered.GuestKey);
        Assert.InRange(answered.Result!.Score, TasteMatch.MinScore, TasteMatch.MaxScore);
        // What both love is named; the suggestion the catalog offered is too, and nothing else of either's.
        Assert.Equal(Shows.Skip(2).Take(4), answered.Result.BothLove.Select(s => s.ShowId).Order().ToList().OrderBy(id => Shows.IndexOf(id)));
        // The second answer finds the match done: the link reads as gone to it, and the first result stands.
        Assert.Null(again);
        Assert.Equal("Robin", (await OpenAsync(token, Guest))!.FriendName);

        await using var db = database.Context();
        var sent = await db.IntegrationEventLogs.AsNoTracking().Where(e => e.Content.Contains(token)).ToListAsync(Ct);
        var news = JsonSerializer.Deserialize<TasteMatchAnsweredIntegrationEvent>(Assert.Single(sent).Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((inviter, token, "Robin", answered.Result.Score), (news.InviterUserId, news.Token, news.FriendName, news.Score));
    }

    [Fact]
    public async Task Too_few_picks_or_picks_that_are_not_shows_do_not_answer_it()
    {
        var token = (await StartAsync()).Token;
        await using var db = database.Context();

        var result = await TasteMatchApi.PickAsync(token, new MatchPicksRequest([Shows[0], Guid.NewGuid(), Guid.NewGuid()], null), Guest, db,
            Catalog(), metrics, time, Ct);

        Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal("open", (await OpenAsync(token, Guest))!.State);
    }

    [Fact]
    public async Task A_member_answers_with_their_own_shows_and_each_is_told_who_the_other_is()
    {
        var token = (await StartAsync()).Token;

        var before = await OpenAsync(token, User(member, "Kenji Sato"));
        var accepted = await AcceptAsync(token, member, "Kenji Sato", unnamed: [Shows[3]]);

        // Their own shows that could be named, before they agree.
        Assert.Equal(5, before!.Nameable!.Count);
        Assert.Equal(("done", "Kenji", "friend", inviter), (accepted!.State, accepted.FriendName, accepted.You, accepted.OtherUserId));
        // Shows 3 to 5 are loved by both; the one they asked not to have named isn't.
        Assert.Equal([Shows[4], Shows[5]], accepted.Result!.BothLove.Select(s => s.ShowId).OrderBy(id => Shows.IndexOf(id)));
        Assert.Equal(("inviter", member), ((await OpenAsync(token, User(inviter, "Ines")))!.You, (await OpenAsync(token, User(inviter, "Ines")))!.OtherUserId));
        // A third person with the link sees the result, and nobody's id.
        Assert.Null((await OpenAsync(token, Guest))!.OtherUserId);
    }

    [Fact]
    public async Task The_inviter_cannot_answer_their_own_match()
    {
        var token = (await StartAsync()).Token;
        await using var db = database.Context();

        var result = await TasteMatchApi.AcceptAsync(token, null, User(inviter, "Ines"), db, Catalog(), Blocks(), metrics, time, Ct);

        Assert.IsType<ProblemHttpResult>(result.Result);
    }

    [Fact]
    public async Task With_a_block_between_them_the_match_reads_as_gone_to_the_other_member()
    {
        var token = (await StartAsync()).Token;
        blocked = true;

        Assert.Null(await OpenAsync(token, User(member, "Kenji")));
        Assert.Null(await AcceptAsync(token, member, "Kenji", unnamed: []));
        // The inviter, and a guest, are nobody's block.
        Assert.NotNull(await OpenAsync(token, User(inviter, "Ines")));
        Assert.NotNull(await OpenAsync(token, Guest));
    }

    [Fact]
    public async Task An_open_link_expires_after_thirty_days_and_a_done_match_is_kept_a_year()
    {
        var open = (await StartAsync()).Token;
        var done = (await StartAsync()).Token;
        await PickAsync(done, Shows.Take(3).ToList(), null);

        time.Advance(TimeSpan.FromDays(31));

        Assert.Null(await OpenAsync(open, Guest));
        Assert.NotNull(await OpenAsync(done, Guest));
        time.Advance(TimeSpan.FromDays(340));
        Assert.Null(await OpenAsync(done, Guest));
        // Starting a match sweeps the expired ones away.
        await StartAsync();
        await using var db = database.Context();
        Assert.False(await db.TasteMatches.AnyAsync(m => m.Token == open || m.Token == done, Ct));
    }

    [Fact]
    public async Task Only_the_two_of_them_or_the_guests_own_device_may_delete_a_match()
    {
        var token = (await StartAsync()).Token;
        var key = (await PickAsync(token, Shows.Take(3).ToList(), null))!.GuestKey!;

        Assert.False(await DeleteAsync(token, User(member, "Kenji"), null));
        Assert.False(await DeleteAsync(token, Guest, null));
        Assert.False(await DeleteAsync(token, Guest, "not-the-key"));
        Assert.NotNull(await OpenAsync(token, Guest));
        Assert.True(await DeleteAsync(token, Guest, key));
        Assert.Null(await OpenAsync(token, Guest));
    }

    [Fact]
    public async Task A_guest_who_makes_an_account_keeps_the_match_and_is_handed_their_picks()
    {
        var token = (await StartAsync()).Token;
        var picks = Shows.Skip(6).Take(3).ToList();
        var key = (await PickAsync(token, picks, "Robin"))!.GuestKey!;
        var signedUp = $"new-{Guid.NewGuid():N}";

        await using var db = database.Context();
        Assert.IsType<NotFound>((await TasteMatchApi.ClaimAsync(token, "wrong", User(signedUp, "Robin"), db, Blocks(), metrics, time, Ct)).Result);
        Assert.IsType<NotFound>((await TasteMatchApi.ClaimAsync(token, key, User(inviter, "Ines"), db, Blocks(), metrics, time, Ct)).Result);
        var claimed = Assert.IsType<Ok<MatchClaimedDto>>((await TasteMatchApi.ClaimAsync(token, key, User(signedUp, "Robin"), db, Blocks(), metrics, time, Ct)).Result).Value!;

        Assert.Equal(("friend", inviter), (claimed.Match.You, claimed.Match.OtherUserId));
        Assert.Equal(picks, claimed.Picks.Select(p => p.ShowId));
        // The key has done its work: it claims nothing twice and deletes nothing now.
        Assert.IsType<NotFound>((await TasteMatchApi.ClaimAsync(token, key, User(member, "Kenji"), db, Blocks(), metrics, time, Ct)).Result);
        Assert.False(await DeleteAsync(token, Guest, key));
        Assert.Contains(await MineAsync(signedUp), m => m.Token == token && m.Role == "friend");
    }

    [Fact]
    public async Task A_members_matches_are_listed_from_either_side_with_the_others_name()
    {
        var token = (await StartAsync()).Token;
        await AcceptAsync(token, member, "Kenji", unnamed: []);
        var waiting = (await StartAsync()).Token;

        var mine = await MineAsync(inviter);
        var theirs = await MineAsync(member);

        Assert.Equal(("done", "inviter", "Kenji"), mine.Where(m => m.Token == token).Select(m => (m.State, m.Role, m.OtherName)).Single());
        Assert.Equal(("open", "inviter", null), mine.Where(m => m.Token == waiting).Select(m => (m.State, m.Role, m.OtherName)).Single());
        Assert.Equal(("done", "friend", "Ines"), theirs.Select(m => (m.State, m.Role, m.OtherName)).Single());
    }

    [Theory]
    [InlineData("Robin", "Robin")]
    [InlineData("  Robin   van der Berg ", "Robin")]
    [InlineData("Ro\u0007bin", "Robin")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_name_shown_to_whoever_has_the_link_is_a_plain_first_name(string? given, string? shown) =>
        Assert.Equal(shown, TasteMatchApi.FirstName(given));

    [Fact]
    public void A_very_long_name_is_cut() => Assert.Equal(30, TasteMatchApi.FirstName(new string('x', 200))!.Length);

    // ---- The calls, each with a context of its own as a request has.

    private static ClaimsPrincipal Guest => new(new ClaimsIdentity());

    private static ClaimsPrincipal User(string id, string name) =>
        new(new ClaimsIdentity([new Claim("sub", id), new Claim("name", name)], authenticationType: "test", nameType: "name", roleType: "role"));

    private async Task<MatchStartedDto> StartAsync()
    {
        await using var db = database.Context();
        return Assert.IsType<Ok<MatchStartedDto>>((await TasteMatchApi.StartAsync(User(inviter, "Ines Castro"), db, metrics, time, Ct)).Result).Value!;
    }

    private async Task<MatchViewDto?> OpenAsync(string token, ClaimsPrincipal user)
    {
        await using var db = database.Context();
        return ((await TasteMatchApi.OpenAsync(token, user, db, Blocks(), time, Ct)).Result as Ok<MatchViewDto>)?.Value;
    }

    private async Task<MatchViewDto?> PickAsync(string token, List<Guid> picks, string? name)
    {
        await using var db = database.Context();
        return ((await TasteMatchApi.PickAsync(token, new MatchPicksRequest(picks, name), Guest, db, Catalog(), metrics, time, Ct)).Result as Ok<MatchViewDto>)?.Value;
    }

    private async Task<MatchViewDto?> AcceptAsync(string token, string userId, string name, List<Guid> unnamed)
    {
        await using var db = database.Context();
        return ((await TasteMatchApi.AcceptAsync(token, new MatchUnnamedRequest(unnamed), User(userId, name), db, Catalog(), Blocks(), metrics, time, Ct))
            .Result as Ok<MatchViewDto>)?.Value;
    }

    private async Task<bool> DeleteAsync(string token, ClaimsPrincipal user, string? key)
    {
        await using var db = database.Context();
        return (await TasteMatchApi.DeleteAsync(token, user, db, time, key, Ct)).Result is NoContent;
    }

    private async Task<List<MyMatchDto>> MineAsync(string userId)
    {
        await using var db = database.Context();
        return (await TasteMatchApi.MineAsync(User(userId, "Someone"), db, time, Ct)).Value!;
    }

    // The catalog says the two are fairly alike and suggests the last three shows.
    private static CatalogMatchClient Catalog() => new(Scripted(JsonSerializer.Serialize(
        new { alike = 0.6, usableFirst = 5, usableSecond = 3, together = Shows.Skip(9), loose = false })));

    private SocialBlocksClient Blocks() => new(Scripted(JsonSerializer.Serialize(new { blocked })));

    private static HttpClient Scripted(string json) => new(new Answering(json)) { BaseAddress = new Uri("http://scripted/") };

    private sealed class Answering(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ratings.API.Services;

namespace Ratings.UnitTests;

public class CatalogLookupTests
{
    private static readonly Guid Show = Guid.NewGuid();
    private static readonly Guid InCast = Guid.NewGuid();

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static CatalogLookup Lookup(Stub handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://catalog/") },
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>(),
        NullLogger<CatalogLookup>.Instance);

    /// <summary>
    /// A catalog that answers everything with <paramref name="body"/>.
    /// </summary>
    private static Stub Says(object body) => new(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

    private static Stub Fails(HttpStatusCode status) => new(() => new HttpResponseMessage(status));

    // As Catalog.API sends a show: the cast among much else.
    private static Stub TheShow() => Says(new { id = Show, title = "x", cast = new[] { new { name = "A", personId = InCast } } });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Someone_in_the_cast_is_and_someone_else_is_not()
    {
        var handler = TheShow();
        var catalog = Lookup(handler);

        Assert.True(await catalog.InCastAsync(Show, InCast, Ct));
        Assert.False(await catalog.InCastAsync(Show, Guid.NewGuid(), Ct));
        // The cast was asked for once, not once per vote.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Nobody_is_in_the_cast_of_a_show_the_catalog_does_not_have() =>
        Assert.False(await Lookup(Fails(HttpStatusCode.NotFound)).InCastAsync(Show, InCast, Ct));

    [Fact]
    public async Task A_catalog_that_fails_gives_no_answer_and_is_asked_again_next_time()
    {
        var handler = Fails(HttpStatusCode.InternalServerError);
        var catalog = Lookup(handler);

        Assert.Null(await catalog.InCastAsync(Show, InCast, Ct));
        Assert.Null(await catalog.InCastAsync(Show, InCast, Ct));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_show_with_nobody_in_its_cast_is_kept_only_briefly()
    {
        // A show whose cast hasn't arrived yet: a run of votes asks once, and the cast counts soon after it arrives.
        var handler = Says(new { id = Show, cast = Array.Empty<object>() });
        var catalog = Lookup(handler);

        Assert.False(await catalog.InCastAsync(Show, InCast, Ct));
        Assert.False(await catalog.InCastAsync(Show, InCast, Ct));
        Assert.Equal(1, handler.Calls);
        Assert.True(CatalogLookup.EmptyKeptFor < CatalogLookup.KeptFor / 10);
    }

    [Fact]
    public async Task A_show_sent_without_a_cast_has_nobody_in_it() =>
        Assert.False(await Lookup(Says(new { id = Show })).InCastAsync(Show, InCast, Ct));

    [Fact]
    public async Task An_episodes_show_is_the_one_the_catalog_names()
    {
        var episode = Guid.NewGuid();
        var catalog = Lookup(Says(new { id = episode, showId = Show }));

        Assert.Equal(Show, await catalog.ShowOfAsync(episode, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_episode_the_catalog_does_not_have_or_cannot_be_asked_about_has_no_show(HttpStatusCode status) =>
        Assert.Null(await Lookup(Fails(status)).ShowOfAsync(Guid.NewGuid(), Ct));

    [Fact]
    public async Task An_episode_sent_without_a_show_has_none() =>
        Assert.Null(await Lookup(Says(new { id = Guid.NewGuid(), showId = Guid.Empty })).ShowOfAsync(Guid.NewGuid(), Ct));

    [Fact]
    public async Task A_request_that_is_cancelled_by_someone_else_gives_no_answer_rather_than_failing()
    {
        // As when another caller of the same cached cast goes away mid-request: not this caller's cancellation.
        var catalog = Lookup(new Stub(() => throw new OperationCanceledException()));

        Assert.Null(await catalog.InCastAsync(Show, InCast, Ct));
        Assert.Null(await catalog.ShowOfAsync(Guid.NewGuid(), Ct));
    }
}

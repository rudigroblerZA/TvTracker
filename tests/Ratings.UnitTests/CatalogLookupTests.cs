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

    private static CatalogLookup Cast(Stub handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://catalog/") },
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>(),
        NullLogger<CatalogLookup>.Instance);

    private static HttpResponseMessage TheShow() => new(HttpStatusCode.OK)
    {
        // As Catalog.API sends a show: the cast among much else.
        Content = JsonContent.Create(new { id = Show, title = "x", cast = new[] { new { name = "A", personId = InCast } } })
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Someone_in_the_cast_is_and_someone_else_is_not()
    {
        var handler = new Stub(TheShow);
        var cast = Cast(handler);

        Assert.True(await cast.InCastAsync(Show, InCast, Ct));
        Assert.False(await cast.InCastAsync(Show, Guid.NewGuid(), Ct));
        // The cast was asked for once, not once per vote.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Nobody_is_in_the_cast_of_a_show_the_catalog_does_not_have() =>
        Assert.False(await Cast(new Stub(() => new HttpResponseMessage(HttpStatusCode.NotFound))).InCastAsync(Show, InCast, Ct));

    [Fact]
    public async Task A_catalog_that_fails_gives_no_answer_and_is_asked_again_next_time()
    {
        var handler = new Stub(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cast = Cast(handler);

        Assert.Null(await cast.InCastAsync(Show, InCast, Ct));
        Assert.Null(await cast.InCastAsync(Show, InCast, Ct));
        Assert.Equal(2, handler.Calls);
    }
}

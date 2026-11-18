using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Research.API.Services;

namespace Research.UnitTests;

/// <summary>
/// The clients for outside sources, registered as Research.API registers them: after the service defaults, which give
/// every client the standard resilience handler. What's asserted is how a client behaves, not how it's put together.
/// </summary>
public class ExternalSourcesTests
{
    private const string Ordinary = "an-ordinary-client";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The service's own registration, with every request answered by <paramref name="answers"/> instead of the network.
    /// </summary>
    private static IHost Build(HttpMessageHandler answers)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.Services.AddExternalSources();
        foreach (var name in new[] { ExternalSources.Client, ExternalSources.LongClient, Ordinary })
        {
            builder.Services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => answers);
        }
        return builder.Build();
    }

    [Fact]
    public void The_clients_identify_themselves_and_keep_their_timeouts()
    {
        using var host = Build(new Refusing());
        var clients = host.Services.GetRequiredService<IHttpClientFactory>();

        using var client = clients.CreateClient(ExternalSources.Client);
        using var longClient = clients.CreateClient(ExternalSources.LongClient);

        Assert.Equal((ExternalSources.Timeout, ExternalSources.LongTimeout), (client.Timeout, longClient.Timeout));
        Assert.All(new[] { client, longClient }, c => Assert.Contains("TvTracker-research", c.DefaultRequestHeaders.UserAgent.ToString()));
    }

    [Fact]
    public void A_source_gives_up_before_the_apps_own_clients_stop_waiting()
    {
        // What a client with the standard handler waits in all: the web app's and the gateway's wait for this service.
        var callersWait = new HttpStandardResilienceOptions().TotalRequestTimeout.Timeout;

        Assert.True(ExternalSources.Timeout < callersWait);
        Assert.True(ExternalSources.LongTimeout > ExternalSources.Timeout);
    }

    [Theory]
    [InlineData(ExternalSources.Client)]
    [InlineData(ExternalSources.LongClient)]
    public async Task A_refusal_is_answered_at_once_not_tried_again(string name)
    {
        var refusing = new Refusing();
        using var host = Build(refusing);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        using var response = await client.GetAsync("https://sources.test/", Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, refusing.Requests);
    }

    /// <summary>
    /// The other side of the test above: the same refusal through a client left as the service defaults make it is
    /// tried again, so one request there does mean the opt-out worked.
    /// </summary>
    [Fact]
    public async Task An_ordinary_client_does_try_a_refusal_again()
    {
        using var secondTry = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var refusing = new Refusing { OnRequest = n => { if (n == 2) { secondTry.Cancel(); } } };
        using var host = Build(refusing);
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(Ordinary);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://sources.test/", secondTry.Token));

        Assert.Equal(2, refusing.Requests);
    }

    /// <summary>
    /// Answers 429 to everything, as the MediaWiki API does to a shared address, and counts the requests.
    /// </summary>
    private sealed class Refusing : HttpMessageHandler
    {
        private int requests;

        public int Requests => requests;

        public Action<int>? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref requests);
            OnRequest?.Invoke(number);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }
    }
}

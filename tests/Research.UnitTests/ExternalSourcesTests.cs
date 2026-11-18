using Microsoft.Extensions.DependencyInjection;
using Research.API.Services;

namespace Research.UnitTests;

public class ExternalSourcesTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // What ServiceDefaults adds to every client; the external clients must opt out of it.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddExternalSources();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(ExternalSources.Client, 20)]
    [InlineData(ExternalSources.LongClient, 70)]
    public void The_clients_have_their_timeout_and_identify_themselves(string name, int seconds)
    {
        using var provider = Build();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        Assert.Equal(TimeSpan.FromSeconds(seconds), client.Timeout);
        Assert.Contains("TvTracker-research", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Theory]
    [InlineData(ExternalSources.Client)]
    [InlineData(ExternalSources.LongClient)]
    public void The_clients_have_no_resilience_handler(string name)
    {
        using var provider = Build();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);

        var names = new List<string>();
        for (var h = handler; h is not null; h = (h as DelegatingHandler)?.InnerHandler)
        {
            names.Add(h.GetType().Name);
        }
        Assert.DoesNotContain(names, n => n.Contains("Resilience", StringComparison.OrdinalIgnoreCase));
    }
}

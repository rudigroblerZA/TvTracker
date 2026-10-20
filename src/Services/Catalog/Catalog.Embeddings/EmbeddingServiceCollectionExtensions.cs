using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.Embeddings;

public static class EmbeddingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local embedding model as <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> and loads it at
    /// startup (in the background, so the service starts straight away).
    /// </summary>
    public static IServiceCollection AddLocalEmbeddings(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmbeddingOptions>(configuration.GetSection("Embeddings"));
        services.AddSingleton<LocalEmbeddingGenerator>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<LocalEmbeddingGenerator>());
        services.AddHostedService<EmbeddingWarmUp>();
        return services;
    }

    private sealed class EmbeddingWarmUp(LocalEmbeddingGenerator generator) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => generator.WarmUpAsync(stoppingToken);
    }
}

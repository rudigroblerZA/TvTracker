using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.Embeddings;

public static class EmbeddingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the configured embedding model as <see cref="EmbeddingModel"/> and
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, and loads it at startup (in the background, so the
    /// service starts straight away). Fails at startup if the model is unknown, incomplete, or doesn't fit
    /// <paramref name="storedDimensions"/>, the size of the vectors the database holds.
    /// </summary>
    public static IServiceCollection AddLocalEmbeddings(this IServiceCollection services, IConfiguration configuration, int storedDimensions)
    {
        var section = configuration.GetSection("Embeddings");
        services.Configure<EmbeddingOptions>(section);
        services.AddSingleton(ResolveModel(section, storedDimensions));
        services.AddSingleton<LocalEmbeddingGenerator>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<LocalEmbeddingGenerator>());
        services.AddHostedService<EmbeddingWarmUp>();
        return services;
    }

    /// <summary>
    /// The model <c>Embeddings:Model</c> names: a built-in one with any settings under <c>Embeddings:Models:{id}</c>
    /// applied on top (e.g. only <c>MaxDistance</c>), or a model defined entirely there.
    /// </summary>
    internal static EmbeddingModel ResolveModel(IConfiguration section, int storedDimensions)
    {
        var id = section["Model"] is { Length: > 0 } configured ? configured : EmbeddingModels.MiniLm;
        var overrides = section.GetSection("Models").GetSection(id);
        if (!EmbeddingModels.BuiltIn.TryGetValue(id, out var builtIn) && !overrides.Exists())
        {
            throw new InvalidOperationException(
                $"Unknown embedding model '{id}'. Use one of {string.Join(", ", EmbeddingModels.BuiltIn.Keys)}, or define it under Embeddings:Models:{id}.");
        }

        var model = builtIn?.Copy() ?? new EmbeddingModel();
        overrides.Bind(model);
        model.Id = id;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(model.BaseUrl)) missing.Add(nameof(model.BaseUrl));
        if (string.IsNullOrWhiteSpace(model.OnnxSha256)) missing.Add(nameof(model.OnnxSha256));
        if (string.IsNullOrWhiteSpace(model.VocabSha256)) missing.Add(nameof(model.VocabSha256));
        if (model.Dimensions <= 0) missing.Add(nameof(model.Dimensions));
        if (model.MaxDistance <= 0) missing.Add(nameof(model.MaxDistance));
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Embedding model '{id}' needs {string.Join(", ", missing)} under Embeddings:Models:{id}.");
        }
        if (!model.BaseUrl.EndsWith('/'))
        {
            // Otherwise the last path segment is replaced when the file paths are resolved against it.
            model.BaseUrl += "/";
        }
        if (model.Dimensions != storedDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding model '{id}' makes {model.Dimensions}-dimension vectors, but the database stores {storedDimensions}. " +
                "Changing that needs a migration (the vector column and its index).");
        }

        return model;
    }

    private sealed class EmbeddingWarmUp(LocalEmbeddingGenerator generator) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => generator.WarmUpAsync(stoppingToken);
    }
}

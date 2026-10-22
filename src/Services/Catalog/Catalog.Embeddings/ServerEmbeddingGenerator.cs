using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Catalog.Embeddings;

/// <summary>Something that can get its model ready at startup, rather than on the first request.</summary>
internal interface IWarmUp
{
    Task WarmUpAsync(CancellationToken ct);
}

/// <summary>
/// Embeddings from a model server with the OpenAI embeddings API, such as Docker Model Runner
/// (<see cref="EmbeddingSource.Server"/>). Vectors longer than the model's <see cref="EmbeddingModel.Dimensions"/> are
/// shortened when <see cref="EmbeddingModel.Truncate"/> allows it, and every vector is normalized, as ONNX ones are.
/// While the server is down (or doesn't have the model), calls throw <see cref="EmbeddingsUnavailableException"/>
/// straight away for a short while, so search doesn't wait on it for every term.
/// </summary>
public sealed class ServerEmbeddingGenerator(EmbeddingModel spec, TimeProvider time, ILogger<ServerEmbeddingGenerator> logger)
    : IEmbeddingGenerator<string, Embedding<float>>, IWarmUp
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    // Its own client, without the standard resilience handler: retrying a server that's down would make every search
    // wait, and the callers already retry (the indexer next round, search without vectors).
    private readonly HttpClient _http = new() { BaseAddress = new Uri(spec.Endpoint), Timeout = TimeSpan.FromSeconds(60) };
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private bool _warned;

    public EmbeddingGeneratorMetadata Metadata { get; } =
        new("openai-compatible", new Uri(spec.Endpoint), spec.Id, spec.Dimensions);

    private static readonly TimeSpan PullRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Embeds a word, so the server loads the model now; logs (once) if it can't. With <see cref="EmbeddingModel.Pull"/>,
    /// first pulls the model if Docker Model Runner doesn't have it, retrying every minute until it can (the server
    /// may still be starting, or Docker Hub refusing).
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (spec.Pull)
        {
            await PullAsync(ct);
        }

        try
        {
            await GenerateAsync(["warm up"], cancellationToken: ct);
            logger.LogInformation("Embedding model {Model} is served by {Endpoint}", spec.ServedModel, spec.Endpoint);
        }
        catch (EmbeddingsUnavailableException)
        {
            // Logged below; retried on next use.
        }
    }

    private async Task PullAsync(CancellationToken ct)
    {
        // Model Runner's own API sits at the root, next to the OpenAI-compatible /engines/v1/.
        using var api = new HttpClient { BaseAddress = new Uri(new Uri(spec.Endpoint), "/"), Timeout = Timeout.InfiniteTimeSpan };
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (var check = await api.GetAsync($"models/{spec.ServedModel}", ct))
                {
                    if (check.IsSuccessStatusCode)
                    {
                        return;
                    }
                }

                logger.LogInformation("Pulling embedding model {Model} into Docker Model Runner; search matches titles only until it's ready",
                    spec.ServedModel);
                using var pull = await api.PostAsJsonAsync("models/create", new PullRequest(spec.ServedModel), ct);
                // The response streams progress, one JSON object a line, ending with "success" or "error"; Model Runner
                // HTML-escapes the quotes (&#34;).
                var body = System.Net.WebUtility.HtmlDecode(await pull.Content.ReadAsStringAsync(ct));
                if (!pull.IsSuccessStatusCode || !body.Contains("\"type\":\"success\"", StringComparison.Ordinal))
                {
                    var last = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                    throw new HttpRequestException($"{(int)pull.StatusCode}: {last[..Math.Min(last.Length, 300)]}");
                }

                logger.LogInformation("Pulled embedding model {Model}", spec.ServedModel);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Logged the first time and then every 10 minutes, so a long outage doesn't fill the log.
                if (failures++ % 10 == 0)
                {
                    logger.LogWarning(ex, "Couldn't pull embedding model {Model} from Docker Model Runner at {Endpoint}; retrying every {Retry}",
                        spec.ServedModel, api.BaseAddress, PullRetry);
                }
            }

            await Task.Delay(PullRetry, time, ct);
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prefix = EmbeddingInput.PrefixFor(spec, options);
        var maxChars = spec.MaxTokens * 4;
        var texts = values.Select(v => prefix + v).Select(t => t.Length > maxChars ? t[..maxChars] : t).ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Count);
        if (texts.Count == 0)
        {
            return result;
        }

        if (time.GetUtcNow() < _retryAt)
        {
            throw new EmbeddingsUnavailableException($"The embedding server at {spec.Endpoint} failed recently; retrying shortly.");
        }

        try
        {
            using var response = await _http.PostAsJsonAsync("embeddings", new EmbeddingRequest(spec.ServedModel, texts), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail[..Math.Min(detail.Length, 300)]}",
                    null, response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
                ?? throw new InvalidDataException("The embedding server returned no body.");
            if (body.Data.Count != texts.Count)
            {
                throw new InvalidDataException($"The embedding server returned {body.Data.Count} vectors for {texts.Count} texts.");
            }

            result.AddRange(body.Data
                .OrderBy(d => d.Index)
                .Select(d => new Embedding<float>(Fit(d.Embedding)) { ModelId = spec.Id, CreatedAt = time.GetUtcNow() }));
            _warned = false;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException
                                   && !cancellationToken.IsCancellationRequested)
        {
            _retryAt = time.GetUtcNow() + RetryAfterFailure;
            if (!_warned)
            {
                // Once per outage: search and the indexer keep calling while the server is down.
                _warned = true;
                logger.LogWarning(ex, "Embedding server {Endpoint} couldn't embed with {Model} (is it running, with the model pulled?); " +
                    "search works without vectors until it can", spec.Endpoint, spec.ServedModel);
            }
            throw new EmbeddingsUnavailableException($"The embedding server at {spec.Endpoint} is unavailable.", ex);
        }
    }

    /// <summary>The vector at the model's length (shortened when it may be), scaled to unit length.</summary>
    private float[] Fit(float[] vector)
    {
        if (vector.Length != spec.Dimensions)
        {
            if (!spec.Truncate || vector.Length < spec.Dimensions)
            {
                throw new InvalidDataException(
                    $"{spec.ServedModel} returns {vector.Length}-dimension vectors; {spec.Id} expects {spec.Dimensions}" +
                    (vector.Length > spec.Dimensions ? " (set Truncate for a model trained to be shortened)." : "."));
            }
            vector = vector[..spec.Dimensions];
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        return norm > 0 ? vector.Select(x => x / norm).ToArray() : vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();

    private sealed record PullRequest([property: JsonPropertyName("from")] string From);

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingData> Data);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Catalog.Embeddings;

/// <summary>Settings for <see cref="LocalEmbeddingGenerator"/>; bound from the "Embeddings" configuration section.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>
    /// Where the model files are kept. Defaults to a per-user cache folder, so Catalog.API and the sync worker
    /// share one download and it survives restarts.
    /// </summary>
    public string? ModelDirectory { get; set; }

    /// <summary>
    /// Which model to use: a built-in one (<see cref="EmbeddingModels.BuiltIn"/>) or one defined under
    /// <c>Models</c>. Catalog.API and the sync worker must use the same one.
    /// </summary>
    public string Model { get; set; } = EmbeddingModels.MiniLm;
}

/// <summary>Whether text is a search term or a document (show text); some models embed the two differently.</summary>
public static class EmbeddingInput
{
    /// <summary>The <see cref="EmbeddingGenerationOptions.AdditionalProperties"/> key; its value is <see cref="Query"/> or <see cref="Document"/>.</summary>
    public const string Key = "input_type";
    public const string Query = "query";
    public const string Document = "document";

    /// <summary>Options that mark the input as search terms. Without them, input is embedded as documents.</summary>
    public static EmbeddingGenerationOptions QueryOptions => new() { AdditionalProperties = new() { [Key] = Query } };
}

/// <summary>The model isn't loaded yet (still downloading, or the download failed). Callers carry on without vectors.</summary>
public sealed class EmbeddingsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Sentence embeddings from the configured <see cref="EmbeddingModel"/> (all-MiniLM-L6-v2 by default), run in-process
/// with ONNX Runtime and normalized, so cosine distance compares them. The model is downloaded and checksummed on
/// first use; until then, and for a few minutes after a failed download, calls throw <see cref="EmbeddingsUnavailableException"/>.
/// </summary>
public sealed class LocalEmbeddingGenerator(
    EmbeddingModel spec, IOptions<EmbeddingOptions> options, TimeProvider time, ILogger<LocalEmbeddingGenerator> logger)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Model? _model;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public EmbeddingGeneratorMetadata Metadata { get; } = new("onnxruntime", defaultModelId: spec.Id, defaultModelDimensions: spec.Dimensions);

    /// <summary>Downloads (if needed) and loads the model now, rather than on the first request.</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            await GetModelAsync(ct);
        }
        catch (EmbeddingsUnavailableException)
        {
            // Logged by GetModelAsync; retried on next use.
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var model = await GetModelAsync(cancellationToken);
        var prefix = options?.AdditionalProperties?.TryGetValue(EmbeddingInput.Key, out var input) == true && Equals(input, EmbeddingInput.Query)
            ? spec.QueryPrefix
            : spec.DocumentPrefix;
        var texts = values.Select(v => prefix + v).ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Count);
        if (texts.Count > 0)
        {
            // CPU-bound; keep it off the request thread's synchronous path.
            var vectors = await Task.Run(() => model.Embed(texts), cancellationToken);
            result.AddRange(vectors.Select(v => new Embedding<float>(v) { ModelId = spec.Id, CreatedAt = time.GetUtcNow() }));
        }
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        _model?.Dispose();
        _loadLock.Dispose();
    }

    private async Task<Model> GetModelAsync(CancellationToken ct)
    {
        if (_model is { } loaded)
        {
            return loaded;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_model is not null)
            {
                return _model;
            }
            if (time.GetUtcNow() < _retryAt)
            {
                throw new EmbeddingsUnavailableException("The embedding model failed to load; retrying shortly.");
            }

            try
            {
                var directory = await EnsureFilesAsync(ct);
                _model = new Model(spec, Path.Combine(directory, Path.GetFileName(spec.OnnxPath)), Path.Combine(directory, Path.GetFileName(spec.VocabPath)));
                logger.LogInformation("Embedding model {Model} loaded from {Directory}", spec.Id, directory);
                return _model;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _retryAt = time.GetUtcNow() + RetryAfterFailure;
                logger.LogWarning(ex, "Couldn't load embedding model {Model}; search works without vectors until it loads", spec.Id);
                throw new EmbeddingsUnavailableException("The embedding model failed to load.", ex);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> EnsureFilesAsync(CancellationToken ct)
    {
        var directory = options.Value.ModelDirectory is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvTracker", "models", spec.Id);
        Directory.CreateDirectory(directory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var (remotePath, sha256) in new[] { (spec.OnnxPath, spec.OnnxSha256), (spec.VocabPath, spec.VocabSha256) })
        {
            var target = Path.Combine(directory, Path.GetFileName(remotePath));
            if (File.Exists(target))
            {
                continue;
            }

            logger.LogInformation("Downloading {File} for embedding model {Model}", remotePath, spec.Id);
            // Downloaded to a temporary name and moved into place once verified, so a process that's stopped
            // mid-download, or another service downloading at the same time, never leaves a partial file behind.
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var file = File.Create(temporary))
                await using (var body = await http.GetStreamAsync(new Uri(new Uri(spec.BaseUrl), remotePath), ct))
                {
                    await body.CopyToAsync(file, ct);
                }

                await using (var file = File.OpenRead(temporary))
                {
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
                    if (actual != sha256)
                    {
                        throw new InvalidDataException($"{remotePath} has checksum {actual}, expected {sha256}.");
                    }
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return directory;
    }

    private sealed class Model : IDisposable
    {
        private readonly EmbeddingModel _spec;
        private readonly InferenceSession _session;
        private readonly BertTokenizer _tokenizer;
        private readonly bool _needsTokenTypes;

        public Model(EmbeddingModel spec, string modelPath, string vocabPath)
        {
            _spec = spec;
            _session = new InferenceSession(modelPath);
            _tokenizer = BertTokenizer.Create(vocabPath);
            _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");

            // The hidden size is fixed in the export; a mismatch means the model and its settings don't belong together.
            var hidden = _session.OutputMetadata[_session.OutputNames[0]].Dimensions[^1];
            if (hidden > 0 && hidden != spec.Dimensions)
            {
                _session.Dispose();
                throw new InvalidDataException($"Model {spec.Id} produces {hidden}-dimension vectors; its settings say {spec.Dimensions}.");
            }
        }

        public List<float[]> Embed(IReadOnlyList<string> texts)
        {
            var tokens = texts
                .Select(text => _tokenizer.EncodeToIds(text, _spec.MaxTokens, out _, out _))
                .ToList();
            var length = tokens.Max(t => t.Count);

            // One padded batch: [CLS] ... [SEP] then zeros, with the attention mask marking the real tokens.
            var ids = new long[texts.Count * length];
            var mask = new long[texts.Count * length];
            for (var row = 0; row < tokens.Count; row++)
            {
                for (var i = 0; i < tokens[row].Count; i++)
                {
                    ids[row * length + i] = tokens[row][i];
                    mask[row * length + i] = 1;
                }
            }

            long[] shape = [texts.Count, length];
            var inputs = new Dictionary<string, OrtValue>
            {
                ["input_ids"] = OrtValue.CreateTensorValueFromMemory(ids, shape),
                ["attention_mask"] = OrtValue.CreateTensorValueFromMemory(mask, shape)
            };
            if (_needsTokenTypes)
            {
                inputs["token_type_ids"] = OrtValue.CreateTensorValueFromMemory(new long[ids.Length], shape);
            }

            try
            {
                using var runOptions = new RunOptions();
                using var outputs = _session.Run(runOptions, inputs, [_session.OutputNames[0]]);
                return Pool(outputs[0].GetTensorDataAsSpan<float>(), mask, texts.Count, length);
            }
            finally
            {
                foreach (var value in inputs.Values)
                {
                    value.Dispose();
                }
            }
        }

        /// <summary>
        /// One vector per text from its token vectors: their average (ignoring padding) or the first token's, per the
        /// model's <see cref="EmbeddingModel.Pooling"/>, scaled to unit length.
        /// </summary>
        private List<float[]> Pool(ReadOnlySpan<float> hidden, long[] mask, int count, int length)
        {
            var dimensions = _spec.Dimensions;
            var vectors = new List<float[]>(count);
            for (var row = 0; row < count; row++)
            {
                var vector = new float[dimensions];
                var tokens = 0;
                // [CLS] is always the first token.
                var pooled = _spec.Pooling == EmbeddingPooling.Cls ? 1 : length;
                for (var i = 0; i < pooled; i++)
                {
                    if (mask[row * length + i] == 0)
                    {
                        continue;
                    }

                    tokens++;
                    var offset = (row * length + i) * dimensions;
                    for (var d = 0; d < dimensions; d++)
                    {
                        vector[d] += hidden[offset + d];
                    }
                }

                var norm = 0f;
                for (var d = 0; d < dimensions; d++)
                {
                    vector[d] /= Math.Max(1, tokens);
                    norm += vector[d] * vector[d];
                }

                norm = MathF.Sqrt(norm);
                if (norm > 0)
                {
                    for (var d = 0; d < dimensions; d++)
                    {
                        vector[d] /= norm;
                    }
                }

                vectors.Add(vector);
            }

            return vectors;
        }

        public void Dispose() => _session.Dispose();
    }
}

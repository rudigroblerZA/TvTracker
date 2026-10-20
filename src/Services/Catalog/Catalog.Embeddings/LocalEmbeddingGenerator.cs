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

    /// <summary>Where the model files are downloaded from: Hugging Face, pinned to a commit so the checksums below hold.</summary>
    public string ModelBaseUrl { get; set; } =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41/";
}

/// <summary>The model isn't loaded yet (still downloading, or the download failed). Callers carry on without vectors.</summary>
public sealed class EmbeddingsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Sentence embeddings from all-MiniLM-L6-v2, run in-process with ONNX Runtime: 384 dimensions, mean-pooled and
/// normalized, so cosine distance compares them. The model (~90 MB) is downloaded and checksummed on first use;
/// until then, and for a few minutes after a failed download, calls throw <see cref="EmbeddingsUnavailableException"/>.
/// </summary>
public sealed class LocalEmbeddingGenerator(IOptions<EmbeddingOptions> options, TimeProvider time, ILogger<LocalEmbeddingGenerator> logger)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Stored with each vector, so a model change re-embeds everything.</summary>
    public const string ModelId = "all-MiniLM-L6-v2";

    public const int Dimensions = 384;

    // The model was trained on up to 256 word pieces; longer text is cut off.
    private const int MaxTokens = 256;

    private static readonly (string Path, string Sha256)[] Files =
    [
        ("onnx/model.onnx", "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
        ("vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3")
    ];

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Model? _model;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public EmbeddingGeneratorMetadata Metadata { get; } = new("onnxruntime", defaultModelId: ModelId, defaultModelDimensions: Dimensions);

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
        var texts = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Count);
        if (texts.Count > 0)
        {
            // CPU-bound; keep it off the request thread's synchronous path.
            var vectors = await Task.Run(() => model.Embed(texts), cancellationToken);
            result.AddRange(vectors.Select(v => new Embedding<float>(v) { ModelId = ModelId, CreatedAt = time.GetUtcNow() }));
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
                _model = new Model(Path.Combine(directory, "model.onnx"), Path.Combine(directory, "vocab.txt"));
                logger.LogInformation("Embedding model {Model} loaded from {Directory}", ModelId, directory);
                return _model;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _retryAt = time.GetUtcNow() + RetryAfterFailure;
                logger.LogWarning(ex, "Couldn't load embedding model {Model}; search works without vectors until it loads", ModelId);
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
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvTracker", "models", ModelId);
        Directory.CreateDirectory(directory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var (remotePath, sha256) in Files)
        {
            var target = Path.Combine(directory, Path.GetFileName(remotePath));
            if (File.Exists(target))
            {
                continue;
            }

            logger.LogInformation("Downloading {File} for embedding model {Model}", remotePath, ModelId);
            // Downloaded to a temporary name and moved into place once verified, so a process that's stopped
            // mid-download, or another service downloading at the same time, never leaves a partial file behind.
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var file = File.Create(temporary))
                await using (var body = await http.GetStreamAsync(new Uri(new Uri(options.Value.ModelBaseUrl), remotePath), ct))
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
        private readonly InferenceSession _session;
        private readonly BertTokenizer _tokenizer;
        private readonly bool _needsTokenTypes;

        public Model(string modelPath, string vocabPath)
        {
            _session = new InferenceSession(modelPath);
            _tokenizer = BertTokenizer.Create(vocabPath);
            _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        }

        public List<float[]> Embed(IReadOnlyList<string> texts)
        {
            var tokens = texts
                .Select(text => _tokenizer.EncodeToIds(text, MaxTokens, out _, out _))
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
                return MeanPool(outputs[0].GetTensorDataAsSpan<float>(), mask, texts.Count, length);
            }
            finally
            {
                foreach (var value in inputs.Values)
                {
                    value.Dispose();
                }
            }
        }

        /// <summary>Averages each text's token vectors (ignoring padding) and scales the result to unit length.</summary>
        private static List<float[]> MeanPool(ReadOnlySpan<float> hidden, long[] mask, int count, int length)
        {
            var vectors = new List<float[]>(count);
            for (var row = 0; row < count; row++)
            {
                var vector = new float[Dimensions];
                var tokens = 0;
                for (var i = 0; i < length; i++)
                {
                    if (mask[row * length + i] == 0)
                    {
                        continue;
                    }

                    tokens++;
                    var offset = (row * length + i) * Dimensions;
                    for (var d = 0; d < Dimensions; d++)
                    {
                        vector[d] += hidden[offset + d];
                    }
                }

                var norm = 0f;
                for (var d = 0; d < Dimensions; d++)
                {
                    vector[d] /= Math.Max(1, tokens);
                    norm += vector[d] * vector[d];
                }

                norm = MathF.Sqrt(norm);
                if (norm > 0)
                {
                    for (var d = 0; d < Dimensions; d++)
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

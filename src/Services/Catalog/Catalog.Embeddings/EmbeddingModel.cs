namespace Catalog.Embeddings;

/// <summary>
/// How a model's per-token vectors become one vector for the text.
/// </summary>
public enum EmbeddingPooling
{
    /// <summary>
    /// The average of the token vectors (sentence-transformers models such as MiniLM).
    /// </summary>
    Mean,

    /// <summary>
    /// The vector of the first ([CLS]) token (BGE and Arctic models).
    /// </summary>
    Cls
}

/// <summary>
/// Where a model runs.
/// </summary>
public enum EmbeddingSource
{
    /// <summary>
    /// In-process with ONNX Runtime; the files are downloaded from <see cref="EmbeddingModel.BaseUrl"/>.
    /// </summary>
    Onnx,

    /// <summary>
    /// Served over the OpenAI embeddings API (<c>POST {Endpoint}embeddings</c>): Docker Model Runner, or another
    /// server with the same API (llama.cpp, Ollama, vLLM).
    /// </summary>
    Server
}

/// <summary>
/// One embedding model: where it runs, how to call it, and how close a match must be. An <see cref="EmbeddingSource.Onnx"/>
/// model must be a BERT-style ONNX export with a WordPiece <c>vocab.txt</c>; a <see cref="EmbeddingSource.Server"/> model
/// is any model the server has. Built-in models are in <see cref="EmbeddingModels"/>; configuration can override
/// their settings or add others under <c>Embeddings:Models:{id}</c>.
/// </summary>
public sealed class EmbeddingModel
{
    /// <summary>
    /// Stored with each vector, so changing the model re-embeds the catalog. Set from the configuration key.
    /// </summary>
    public string Id { get; set; } = "";

    public EmbeddingSource Source { get; set; } = EmbeddingSource.Server;

    /// <summary>
    /// The server's OpenAI-compatible base address, for <see cref="EmbeddingSource.Server"/>. Docker Model Runner's
    /// from the host (Docker Desktop with host-side TCP enabled, or Docker Engine's model runner) by default.
    /// </summary>
    public string Endpoint { get; set; } = EmbeddingModels.DockerModelRunnerEndpoint;

    /// <summary>The model's name on the server, e.g. <c>ai/nomic-embed-text-v1.5</c>.</summary>
    public string ServedModel { get; set; } = "";

    /// <summary>
    /// The server is Docker Model Runner, which can pull <see cref="ServedModel"/> from Docker Hub when it doesn't
    /// have it (its <c>/models/create</c> API, next to <c>/engines/v1/</c>). Only the services allowed to pull do so.
    /// </summary>
    public bool Pull { get; set; }

    /// <summary>
    /// Keep the first <see cref="Dimensions"/> of a longer vector and re-normalize it. Only for models trained for it
    /// (Matryoshka models such as nomic-embed-text-v1.5); for others the shortened vectors lose most of their meaning.
    /// </summary>
    public bool Truncate { get; set; }

    /// <summary>Where the ONNX files are downloaded from; pin it to a commit so the checksums hold.</summary>
    public string BaseUrl { get; set; } = "";

    public string OnnxPath { get; set; } = "onnx/model.onnx";

    public string OnnxSha256 { get; set; } = "";

    public string VocabPath { get; set; } = "vocab.txt";

    public string VocabSha256 { get; set; } = "";

    /// <summary>Length of the vectors; must match the <c>show_embeddings</c> column.</summary>
    public int Dimensions { get; set; }

    /// <summary>
    /// Tokens the model was trained on; longer text is cut off. For a server model, text is cut at about four
    /// characters a token before it's sent, since some servers refuse input longer than their batch.
    /// </summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>
    /// For ONNX models; a server pools by itself.
    /// </summary>
    public EmbeddingPooling Pooling { get; set; } = EmbeddingPooling.Mean;

    /// <summary>
    /// Put in front of search terms (some models are trained to tell queries from documents this way).
    /// </summary>
    public string QueryPrefix { get; set; } = "";

    /// <summary>
    /// Put in front of the show text that's embedded.
    /// </summary>
    public string DocumentPrefix { get; set; } = "";

    /// <summary>
    /// Cosine distance (0 = same meaning) beyond which a show isn't a match. Each model spreads its distances
    /// differently, so this is tuned per model on TVmaze shows.
    /// </summary>
    public double MaxDistance { get; set; }

    internal EmbeddingModel Copy() => (EmbeddingModel)MemberwiseClone();
}

public static class EmbeddingModels
{
    public const string MiniLm = "all-MiniLM-L6-v2";
    public const string BgeSmall = "bge-small-en-v1.5";
    public const string DockerNomic = "dmr/nomic-embed-text-v1.5";
    public const string DockerQwen3 = "dmr/qwen3-embedding-0.6b";

    /// <summary>
    /// Docker Model Runner's OpenAI-compatible API, as seen from the host.
    /// </summary>
    public const string DockerModelRunnerEndpoint = "http://localhost:12434/engines/v1/";

    /// <summary>
    /// The models that work without configuring files; <see cref="EmbeddingOptions.Model"/> picks one.
    /// </summary>
    public static IReadOnlyDictionary<string, EmbeddingModel> BuiltIn { get; } = new Dictionary<string, EmbeddingModel>
    {
        // ~90 MB; the default. Related shows land around 0.3-0.64 ("cooking competition" → The Great British Bake Off at 0.62),
        // unrelated ones from about 0.66.
        [MiniLm] = new()
        {
            Id = MiniLm,
            BaseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41/",
            OnnxSha256 = "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452",
            VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
            Dimensions = 384,
            MaxTokens = 256,
            Pooling = EmbeddingPooling.Mean,
            MaxDistance = 0.65
        },
        // ~130 MB, about twice as slow, and spreads distances tighter: related shows land around 0.22-0.50 ("cooking
        // competition" → Easy-Bake Battle at 0.22, "chemistry teacher becomes a drug dealer" → Breaking Bad at 0.49),
        // unrelated searches ("knitting patterns for beginners") from about 0.46. Trained with an instruction in front
        // of queries, and none on documents.
        [BgeSmall] = new()
        {
            Id = BgeSmall,
            BaseUrl = "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a/",
            OnnxSha256 = "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35",
            VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
            Dimensions = 384,
            MaxTokens = 512,
            Pooling = EmbeddingPooling.Cls,
            QueryPrefix = "Represent this sentence for searching relevant passages: ",
            MaxDistance = 0.45
        },
        // Served by Docker Model Runner (docker model pull ai/nomic-embed-text-v1.5; ~270 MB). 768 dimensions, trained
        // to be shortened, so it's cut to the column's 384. Trained with these task prefixes. Docker Model Runner's
        // llama.cpp refuses input longer than its 512-token batch, so show text is cut to that. On the 42 test
        // searches over 595 shows it ranked below the ONNX models (MRR 0.63, 0.67 uncut) and is ~10x slower per
        // search; related shows land around 0.25-0.38, unrelated searches from about 0.38 (title words such as
        // "list" or "location" pull them close).
        [DockerNomic] = new()
        {
            Id = DockerNomic,
            Source = EmbeddingSource.Server,
            ServedModel = "ai/nomic-embed-text-v1.5",
            Pull = true,
            Dimensions = 384,
            Truncate = true,
            MaxTokens = 512,
            QueryPrefix = "search_query: ",
            DocumentPrefix = "search_document: ",
            MaxDistance = 0.38
        },
        // The default. Served by Docker Model Runner (docker model pull ai/qwen3-embedding:0.6b; Q8, ~640 MB). 1024
        // dimensions, trained to be shortened (MRL), so it's cut to the column's 384. Instruction-aware: search terms
        // take an "Instruct: …\nQuery:" line, show text none. On 41 test searches over 846 shows it put the expected
        // show first for 30 and in the top 3 for 34 (MRR 0.78; nomic 18, 27 and 0.57), at ~130 ms a search on a CPU.
        // Expected shows land around 0.40 (90th percentile 0.455), unrelated searches ("knitting patterns for
        // beginners") from 0.463. A batch of 32 shows takes ~25 s on 4 cores, so the first embedding of the catalog
        // takes about eight minutes.
        [DockerQwen3] = new()
        {
            Id = DockerQwen3,
            Source = EmbeddingSource.Server,
            ServedModel = "ai/qwen3-embedding:0.6b",
            Pull = true,
            Dimensions = 384,
            Truncate = true,
            MaxTokens = 512,
            QueryPrefix = "Instruct: Given a description of a TV show, find TV shows that match it\nQuery:",
            MaxDistance = 0.46
        }
    };
}

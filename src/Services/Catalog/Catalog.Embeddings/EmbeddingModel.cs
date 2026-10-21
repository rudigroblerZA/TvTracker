namespace Catalog.Embeddings;

/// <summary>How a model's per-token vectors become one vector for the text.</summary>
public enum EmbeddingPooling
{
    /// <summary>The average of the token vectors (sentence-transformers models such as MiniLM).</summary>
    Mean,

    /// <summary>The vector of the first ([CLS]) token (BGE and Arctic models).</summary>
    Cls
}

/// <summary>
/// One embedding model: where its files come from, how to run it, and how close a match must be. The model must be a
/// BERT-style ONNX export with a WordPiece <c>vocab.txt</c>. Built-in models are in <see cref="EmbeddingModels"/>;
/// configuration can override their settings or add others under <c>Embeddings:Models:{id}</c>.
/// </summary>
public sealed class EmbeddingModel
{
    /// <summary>Stored with each vector, so changing the model re-embeds the catalog. Set from the configuration key.</summary>
    public string Id { get; set; } = "";

    /// <summary>Where the files are downloaded from; pin it to a commit so the checksums hold.</summary>
    public string BaseUrl { get; set; } = "";

    public string OnnxPath { get; set; } = "onnx/model.onnx";

    public string OnnxSha256 { get; set; } = "";

    public string VocabPath { get; set; } = "vocab.txt";

    public string VocabSha256 { get; set; } = "";

    /// <summary>Length of the vectors; must match the <c>show_embeddings</c> column.</summary>
    public int Dimensions { get; set; }

    /// <summary>Word pieces the model was trained on; longer text is cut off.</summary>
    public int MaxTokens { get; set; } = 256;

    public EmbeddingPooling Pooling { get; set; } = EmbeddingPooling.Mean;

    /// <summary>Put in front of search terms (some models are trained to tell queries from documents this way).</summary>
    public string QueryPrefix { get; set; } = "";

    /// <summary>Put in front of the show text that's embedded.</summary>
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

    /// <summary>The models that work without configuring files; <see cref="EmbeddingOptions.Model"/> picks one.</summary>
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
        }
    };
}

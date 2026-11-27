using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.UnitTests;

/// <summary>
/// Every built-in embedding model can be picked with <c>Embeddings:Model</c> alone: none needs settings of its own
/// to get past the start-up check.
/// </summary>
public class EmbeddingModelsTests
{
    public static TheoryData<string> BuiltIn => new(EmbeddingModels.BuiltIn.Keys);

    [Theory]
    [MemberData(nameof(BuiltIn))]
    public void A_built_in_model_is_accepted_by_name_alone(string id)
    {
        var model = Resolve(new() { ["Embeddings:Model"] = id });

        Assert.Equal(id, model.Id);
    }

    [Theory]
    [InlineData(EmbeddingModels.MiniLm)]
    [InlineData(EmbeddingModels.BgeSmall)]
    public void The_downloaded_models_run_in_process(string id) =>
        Assert.Equal(EmbeddingSource.Onnx, Resolve(new() { ["Embeddings:Model"] = id }).Source);

    [Fact]
    public void With_no_model_named_the_in_process_default_is_used() =>
        Assert.Equal(EmbeddingModels.MiniLm, Resolve([]).Id);

    private static EmbeddingModel Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLocalEmbeddings(configuration, ShowEmbedding.Dimensions);
        return (EmbeddingModel)services.Single(s => s.ServiceType == typeof(EmbeddingModel)).ImplementationInstance!;
    }
}

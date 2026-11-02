using Catalog.SyncWorker.Nanogenres;

namespace Catalog.UnitTests;

public class NanogenreSlugTests
{
    [Theory]
    [InlineData("Medical drama", "medical-drama")]
    [InlineData("Animated family comedy", "animated-family-comedy")]
    [InlineData("Children's adventure", "childrens-adventure")]
    [InlineData("  Sci-Fi & fantasy! ", "sci-fi-fantasy")]
    public void Slugs(string name, string slug) => Assert.Equal(slug, NanogenreDiscovery.Slug(name));
}

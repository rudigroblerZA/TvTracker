using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// What the footer, the terms and the privacy policy say about each catalog source.
/// </summary>
public class CatalogSourceInfoTests
{
    [Theory]
    [InlineData("tvmaze", "TVmaze", "TVmaze, Wikidata and Wikiquote")]
    [InlineData("tvdb", "TheTVDB", "TheTVDB, Wikidata and Wikiquote")]
    // Wikidata is the source and one of the two every catalog takes facts from: named once.
    [InlineData("wikidata", "Wikidata", "Wikidata, Wikipedia and Wikiquote")]
    [InlineData("mock", "our development catalog", "our development catalog, Wikidata and Wikiquote")]
    public void Each_source_is_named_and_lists_where_data_comes_from(string source, string name, string dataFrom)
    {
        var info = new Branding(catalogSource: source).Catalog;

        Assert.Equal(name, info.Name);
        Assert.Equal(dataFrom, info.DataFrom);
    }

    [Fact]
    public void The_footer_credits_the_source_and_whoever_comes_with_it()
    {
        Assert.Equal(["TVmaze"], CatalogSourceInfo.For("tvmaze").Credits.Select(c => c.Name));
        // Wikidata's overviews are Wikipedia's, which is CC BY-SA and so credited with its own link.
        Assert.Equal([("Wikidata", "https://www.wikidata.org"), ("Wikipedia", "https://en.wikipedia.org")],
            CatalogSourceInfo.For("wikidata").Credits.Select(c => (c.Name, c.Url)));
        Assert.Empty(CatalogSourceInfo.For("mock").Credits);
    }

    [Fact]
    public void Each_source_has_its_licence_and_an_unknown_one_reads_as_TVmaze()
    {
        Assert.Equal(CatalogLicence.CcBySa, CatalogSourceInfo.For("tvmaze").Licence);
        Assert.Equal(CatalogLicence.Own, CatalogSourceInfo.For("tvdb").Licence);
        Assert.Equal(CatalogLicence.Invented, CatalogSourceInfo.For("mock").Licence);
        Assert.Equal("TVmaze", new Branding(catalogSource: " TVMaze ").Catalog.Name);
        Assert.Equal("TVmaze", new Branding(catalogSource: "something-else").Catalog.Name);
    }
}

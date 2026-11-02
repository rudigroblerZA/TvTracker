using Catalog.Data;

namespace Catalog.UnitTests;

/// <summary>
/// Where-to-watch links: a show's page only under a known service's show-page path (see CONTEXT.md, Where to watch).
/// </summary>
public class StreamingServicesTests
{
    [Theory]
    [InlineData("https://www.netflix.com/title/80057281")]
    [InlineData("https://netflix.com/title/80057281")]
    public void Show_pages(string url) => Assert.True(StreamingServices.IsTitlePage(url));

    [Theory]
    [InlineData("https://www.netflix.com/")]
    [InlineData("https://www.netflix.com/browse")]
    [InlineData("https://www.netflix.com/title/")]
    [InlineData("https://www.amc.com/shows/breaking-bad")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void Not_show_pages(string? url) => Assert.False(StreamingServices.IsTitlePage(url));

    [Fact]
    public void A_known_service_is_found_by_host()
    {
        Assert.Equal("Netflix", StreamingServices.For("https://www.netflix.com/title/80057281")?.Name);
        Assert.Null(StreamingServices.For("https://www.amc.com/"));
    }

    [Fact]
    public void Search_fills_in_the_title() =>
        Assert.Equal("https://www.netflix.com/search?q=Black%20Doves", StreamingServices.SearchFor("https://www.netflix.com/", "Netflix", "Black Doves"));
}

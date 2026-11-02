using Catalog.SyncWorker.Providers;

namespace Catalog.UnitTests;

/// <summary>TVmaze summaries arrive as HTML, some escaped twice (see CONTEXT.md, Summaries).</summary>
public class StripHtmlTests
{
    [Theory]
    [InlineData("<p><b>Walter White</b>, a chemistry teacher.</p>", "Walter White , a chemistry teacher.")]
    [InlineData("<p>Walter &amp; Jesse</p>", "Walter & Jesse")]
    [InlineData("<p>Walter &amp;amp; Jesse</p>", "Walter & Jesse")]
    [InlineData("Line one<br/>\n\n  line two", "Line one line two")]
    public void Plain_text(string html, string text) => Assert.Equal(text, TvMazeCatalogProvider.StripHtml(html));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("<p></p>")]
    public void Nothing_left_is_null(string? html) => Assert.Null(TvMazeCatalogProvider.StripHtml(html));
}

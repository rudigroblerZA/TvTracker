using Catalog.API.Services;

namespace Catalog.UnitTests;

/// <summary>
/// What's typed into search becomes a LIKE pattern; wildcards someone typed are taken literally.
/// </summary>
public class TitleSearchTests
{
    [Theory]
    [InlineData("  lost ", "lost")]
    [InlineData("100%", @"100\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData(@"back\slash", @"back\\slash")]
    public void A_terms_own_wildcards_are_escaped(string term, string escaped) =>
        Assert.Equal(escaped, TitleSearch.Escape(term));
}

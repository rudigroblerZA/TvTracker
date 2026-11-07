using Research.API.Services;

namespace Research.UnitTests;

/// <summary>
/// The daily check's loose matches, with CONTEXT.md's cases: a network by containment or initials, a premiere at
/// Wikidata's precision, episodes within 2 or 10%. Too strict lists every show as different; too loose hides the
/// differences a moderator should see.
/// </summary>
public class CatalogChecksTests
{
    [Theory]
    [InlineData("HBO", "HBO Max")]
    [InlineData("Apple TV+", "Apple TV")]
    [InlineData("American Broadcasting Company", "ABC")]
    [InlineData("British Broadcasting Corporation", "BBC One")]
    [InlineData("Fox Broadcasting Company", "FOX")]
    [InlineData("Channel 4 Television Corporation", "Channel 4 TV")]
    public void A_network_matches_by_containment_or_initials(string wikidata, string catalog) =>
        Assert.True(CatalogChecks.SameNetwork(wikidata, catalog));

    [Theory]
    [InlineData("NBC", "CBS")]
    [InlineData("BBC iPlayer", "BBC One")]      // a streaming home against a first broadcaster: a moderator's call
    [InlineData("Netflix", "")]
    [InlineData("Netflix", null)]
    public void Other_networks_differ(string wikidata, string? catalog) =>
        Assert.False(CatalogChecks.SameNetwork(wikidata, catalog));

    [Fact]
    public void A_premiere_is_compared_at_wikidatas_precision()
    {
        var day = CatalogChecks.Earliest("2013-01-20T00:00:00Z@11|2012-10-01T00:00:00Z@10")!;
        // The earliest start wins, with its own precision: a month-only date is stored as the 1st.
        Assert.Equal(new DateOnly(2012, 10, 1), day.Date);
        Assert.Equal(10, day.Precision);
        Assert.True(CatalogChecks.SameStart(day, new DateOnly(2012, 10, 24)));
        Assert.False(CatalogChecks.SameStart(day, new DateOnly(2012, 11, 1)));

        // A day apart is time zones; two is a difference.
        var exact = new CatalogChecks.WikidataDate(new DateOnly(2008, 1, 20), 11);
        Assert.True(CatalogChecks.SameStart(exact, new DateOnly(2008, 1, 21)));
        Assert.False(CatalogChecks.SameStart(exact, new DateOnly(2008, 1, 22)));
        Assert.True(CatalogChecks.SameStart(new CatalogChecks.WikidataDate(new DateOnly(2008, 1, 1), 9), new DateOnly(2008, 7, 1)));
        Assert.False(CatalogChecks.SameStart(exact, null));
    }

    [Theory]
    [InlineData(10, 12, true)]      // within 2
    [InlineData(10, 13, false)]
    [InlineData(100, 110, true)]    // within 10%
    [InlineData(100, 111, false)]
    public void Episode_counts_agree_within_2_or_10_percent(int wikidata, int catalog, bool same) =>
        Assert.Equal(same, CatalogChecks.SameEpisodes(wikidata, catalog));
}

using Catalog.SyncWorker.Providers.Wikidata;
using TvTracker.IntegrationEvents;

namespace Catalog.UnitTests;

/// <summary>
/// Wikidata's statements read as the catalog's fields (CONTEXT.md, Catalog sync: Wikidata), with the shows the rules
/// were checked on.
/// </summary>
public class WikidataMappingTests
{
    [Fact]
    public void Genre_items_read_as_TVmazes_genres()
    {
        // Breaking Bad's and Severance's genres as Wikidata labels them.
        Assert.Equal(["Drama", "Crime", "Western"],
            WikidataMapping.Genres(["drama television series", "crime drama", "neo-Western"]));
        Assert.Equal(["Science-Fiction", "Thriller", "Comedy", "Drama"],
            WikidataMapping.Genres(["science fiction television series", "psychological thriller", "comedy-drama"]));
        Assert.Empty(WikidataMapping.Genres(["software", "television program"]));
    }

    [Fact]
    public void A_shows_kind_comes_from_its_type_then_its_genres()
    {
        Assert.Equal("animation", WikidataMapping.Kind(["Q581714"], ["sitcom"]));
        Assert.Equal("reality", WikidataMapping.Kind(["Q5398426"], ["reality television"]));
        // The Office (UK) is a mockumentary, not a documentary.
        Assert.Equal("scripted", WikidataMapping.Kind(["Q5398426"], ["mockumentary", "pseudo-documentary", "sitcom"]));
        Assert.Equal("documentary", WikidataMapping.Kind(["Q5398426"], ["documentary television series"]));
    }

    [Fact]
    public void Status_language_gender_and_dates()
    {
        var today = new DateOnly(2026, 11, 9);
        Assert.Equal(ShowStatus.Ended, WikidataMapping.Status(new(2008, 1, 20), new(2013, 9, 29), today));
        Assert.Equal(ShowStatus.Airing, WikidataMapping.Status(new(2022, 2, 18), null, today));
        Assert.Equal(ShowStatus.Upcoming, WikidataMapping.Status(null, null, today));

        Assert.Equal("english", WikidataMapping.Language("American English"));
        Assert.Equal("Female", WikidataMapping.Gender("http://www.wikidata.org/entity/Q6581072"));
        Assert.Null(WikidataMapping.Gender("http://www.wikidata.org/entity/Q48270"));
        Assert.Equal(new DateOnly(2008, 1, 20), WikidataMapping.Date("2008-01-20T00:00:00Z"));
        Assert.Null(WikidataMapping.Date("-0500-01-01T00:00:00Z"));
    }

    [Fact]
    public void Episodes_need_a_season_and_number_and_one_item_takes_each_place()
    {
        var episodes = WikidataMapping.Episodes(
        [
            new("Q2", "Cat's in the Bag...", 1, 2, new(2008, 1, 27), 48),
            new("Q1", "Pilot", 1, 1, null, 58),
            new("Q9", "Pilot (duplicate)", 1, 1, new(2008, 1, 20), null),
            new("Q5", "No season", null, 3, null, null),
            new("Q6", "Q6", 2, 1, null, 900)
        ]);

        Assert.Equal(["Q9", "Q2", "Q6"], episodes.Select(e => e.ExternalId));
        Assert.Equal(new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero), episodes[0].AirsAtUtc);
        // An untitled item's label is its id; an implausible runtime is left out.
        Assert.Equal(("Episode 1", (int?)null), (episodes[2].Title, episodes[2].RuntimeMinutes));
    }
}

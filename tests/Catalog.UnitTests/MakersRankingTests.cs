using Catalog.API.Apis;
using TvTracker.IntegrationEvents;

namespace Catalog.UnitTests;

/// <summary>
/// From the makers you love: makers weighed by the love of their shows, picks by that love and their rating.
/// </summary>
public class MakersRankingTests
{
    private static readonly Guid BreakingBad = Guid.NewGuid(), Saul = Guid.NewGuid(), Fleabag = Guid.NewGuid(), Office = Guid.NewGuid();
    private static readonly Guid Gilligan = Guid.NewGuid(), Gould = Guid.NewGuid(), WallerBridge = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> Titles = new()
    {
        [BreakingBad] = "Breaking Bad", [Saul] = "Better Call Saul", [Fleabag] = "Fleabag", [Office] = "The Office"
    };

    [Fact]
    public void A_maker_behind_two_favourites_outranks_one_behind_a_followed_show()
    {
        var makers = new[]
        {
            Maker(Gilligan, "Vince Gilligan", [BreakingBad, Saul],
                Made("169", BreakingBad, 9.2), Made("618", Saul, 8.6), Made("734", null, 7.3, from: 2015), Made("86175", null, 7.8, isNew: true)),
            Maker(Gould, "Peter Gould", [Saul], Made("618", Saul, 8.6), Made("86175", null, 7.8, isNew: true)),
            Maker(WallerBridge, "Phoebe Waller-Bridge", [Fleabag], Made("fb", Fleabag, 8.7), Made("ke", null, 8.2, from: 2018))
        };
        LovedShowInput[] loved = [new(BreakingBad, 1), new(Saul, 0.8), new(Fleabag, 0.5)];

        var result = MakersRanking.Build(loved, new HashSet<Guid> { BreakingBad, Saul, Fleabag }, Titles, makers);

        Assert.Equal(["Vince Gilligan", "Peter Gould", "Phoebe Waller-Bridge"], result.Makers.Select(m => m.Name));
        Assert.Equal(1.8, result.Makers[0].Love);
        Assert.Equal(2, result.Makers[0].OtherShows);

        // Pluribus is new, and made by two of the makers: listed once, with both.
        var pluribus = Assert.Single(result.New);
        Assert.Equal(["Vince Gilligan", "Peter Gould"], pluribus.By.Select(b => b.Name));
        Assert.Equal("Breaking Bad", pluribus.By[0].Because.Title);
        Assert.Equal(Math.Round((1.8 + 0.8) * 7.8 / 10, 3), pluribus.Score);

        // Battle Creek: 1.8 × 7.3 / 10 = 1.314; Killing Eve: 0.5 × 8.2 / 10 = 0.41.
        Assert.Equal(["734", "ke"], result.Picks.Select(p => p.Show.ExternalId));
    }

    [Fact]
    public void Shows_the_caller_knows_are_left_out()
    {
        var makers = new[] { Maker(Gilligan, "Vince Gilligan", [BreakingBad], Made("169", BreakingBad, 9.2), Made("618", Saul, 8.6)) };

        var result = MakersRanking.Build([new(BreakingBad, 1)], new HashSet<Guid> { BreakingBad, Saul }, Titles, makers);

        Assert.Empty(result.Picks);
        Assert.Empty(result.New);
        Assert.Equal(0, result.Makers[0].OtherShows);
    }

    [Fact]
    public void An_unrated_show_counts_as_a_seven()
    {
        var makers = new[] { Maker(Gilligan, "Vince Gilligan", [BreakingBad], Made("x", null, null, from: 2010)) };

        var pick = Assert.Single(MakersRanking.Build([new(BreakingBad, 1)], new HashSet<Guid>(), Titles, makers).Picks);

        Assert.Equal(MakersRanking.UnratedRating / 10, pick.Score);
    }

    [Fact]
    public void Makers_still_being_fetched_are_counted()
    {
        var makers = new[] { Maker(Gilligan, "Vince Gilligan", [BreakingBad], pending: true) };

        var result = MakersRanking.Build([new(BreakingBad, 1)], new HashSet<Guid>(), Titles, makers);

        Assert.Equal((1, 1), (result.LovedShows, result.MakersPending));
        Assert.Empty(result.Picks);
        // Listed, but as not in yet rather than as having made nothing else.
        Assert.True(Assert.Single(result.Makers).Pending);
    }

    [Fact]
    public void Coming_shows_are_listed_soonest_first_and_undated_projects_are_left_out()
    {
        var makers = new[]
        {
            Maker(Gilligan, "Vince Gilligan", [BreakingBad],
                Coming("2029", 2029), Coming("2027", 2027), Coming("pitch", null), Made("recent", null, 8, isNew: true))
        };

        var result = MakersRanking.Build([new(BreakingBad, 1)], new HashSet<Guid>(), Titles, makers);

        Assert.Equal(["2027", "2029", "recent"], result.New.Select(p => p.Show.ExternalId));
        Assert.DoesNotContain(result.Picks, p => p.Show.ExternalId == "pitch");
        Assert.Equal(3, result.Makers[0].OtherShows);
    }

    [Fact]
    public void A_makers_shows_read_coming_then_newest_then_in_development()
    {
        MadeShowDto[] shows = [Made("old", null, 8, from: 2001), Coming("pitch", null), Made("newer", null, 8, from: 2015), Coming("2029", 2029),
            Coming("2027", 2027)];

        var ordered = shows.OrderBy(MakersRanking.Order).ThenBy(MakersRanking.OrderYear).Select(s => s.ExternalId);

        Assert.Equal(["2027", "2029", "newer", "old", "pitch"], ordered);
    }

    private static MadeShowDto Coming(string externalId, int? from) =>
        new(null, externalId, externalId, null, from, null, ShowStatus.Upcoming, null, 90, from is not null);

    private static MakersRanking.Maker Maker(Guid id, string name, Guid[] loved, params MadeShowDto[] made) =>
        new(id, name, null, false, loved, made);

    private static MakersRanking.Maker Maker(Guid id, string name, Guid[] loved, bool pending) => new(id, name, null, pending, loved, []);

    private static MadeShowDto Made(string externalId, Guid? showId, double? rating, int from = 2008, bool isNew = false) =>
        new(showId, externalId, externalId, null, isNew ? 2025 : from, null, isNew ? ShowStatus.Airing : ShowStatus.Ended, rating, 90, isNew);
}

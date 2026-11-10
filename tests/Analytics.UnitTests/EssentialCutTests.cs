using Analytics.API.Apis;
using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.UnitTests.Build;

namespace Analytics.UnitTests;

/// <summary>
/// The essential cut at "lean".
/// </summary>
public class EssentialCutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset First = new(2024, 1, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<Guid, EpisodeNoteCopy> NoNotes = new Dictionary<Guid, EpisodeNoteCopy>();

    [Fact]
    public void Skips_a_clearly_weak_episode_and_protects_premiere_best_and_finale()
    {
        var show = Show();
        var e = Season(show, 1, First, 7.0, 8.0, 8.0, 6.0, 8.0, 8.0, 8.0, 9.0, 8.0, 8.0);

        var cut = Decide(show, e);

        Assert.Equal((false, "below-average"), cut[e[3].EpisodeId]);
        Assert.Equal((true, "series-premiere"), cut[e[0].EpisodeId]);
        Assert.Equal((true, "best-of-season"), cut[e[7].EpisodeId]);
        Assert.Equal((true, "season-finale"), cut[e[9].EpisodeId]);
        Assert.Equal(1, cut.Values.Count(d => !d.Keep));
    }

    [Fact]
    public void An_ended_shows_last_finale_is_the_series_finale()
    {
        var show = Show(ShowStatus.Ended);
        var e = Season(show, 1, First, 7.0, 8.0, 8.0, 6.0, 8.0, 8.0, 8.0, 9.0, 8.0, 8.0);

        Assert.Equal((true, "series-finale"), Decide(show, e)[e[9].EpisodeId]);
    }

    [Fact]
    public void A_season_still_airing_has_no_finale_yet()
    {
        var show = Show();
        var e = Season(show, 1, Now.AddDays(-63), 8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 6.0, null);

        var cut = Decide(show, e);

        // The tenth is the last aired, not the finale: an eleventh is still to come.
        Assert.Equal((false, "below-average"), cut[e[9].EpisodeId]);
    }

    [Fact]
    public void Two_parters_go_together()
    {
        var show = Show();
        var e = Season(show, 1, First, 7.0, 8.0, 8.0, 6.0, 8.0, 8.0, 8.0, 9.0, 8.0, 8.0);
        e[3].Title = "Blood, Part 1";
        e[4].Title = "Blood, Part 2";

        var cut = Decide(show, e);

        Assert.Equal((true, "two-parter"), cut[e[3].EpisodeId]);
        Assert.Equal((true, "two-parter"), cut[e[4].EpisodeId]);
        Assert.All(cut.Values, d => Assert.True(d.Keep));
    }

    [Fact]
    public void An_evenly_rated_season_loses_nothing_to_noise()
    {
        var show = Show();
        var e = Season(show, 1, First, 7.8, 7.7, 7.8, 7.9, 7.7, 7.8, 7.8, 7.9, 7.7, 7.8);

        Assert.All(Decide(show, e).Values, d => Assert.True(d.Keep));
    }

    [Fact]
    public void A_moderators_notes_decide_the_season_alone()
    {
        var show = Show();
        var e = Season(show, 1, First, 7.0, 8.0, 8.0, 6.0, 8.0, 8.0, 8.0, 9.0, 8.0, 8.0);
        var notes = new Dictionary<Guid, EpisodeNoteCopy>
        {
            [e[5].EpisodeId] = Note(e[5], EpisodeMarks.Skippable),
            [e[6].EpisodeId] = Note(e[6], EpisodeMarks.Skippable)
        };

        var cut = EssentialCutApi.Decide(show, e, e, x => x.ExternalRating, EssentialCutApi.Lean, Now, notes);

        Assert.Equal(new[] { e[5].EpisodeId, e[6].EpisodeId }.Order(), cut.Where(d => !d.Value.Keep).Select(d => d.Key).Order());
        Assert.Equal((false, "noted-skippable"), cut[e[5].EpisodeId]);
        // The weak episode the ratings would cut stays: the notes said what to skip.
        Assert.True(cut[e[3].EpisodeId].Keep);
    }

    [Theory]
    [InlineData(19, 19, "It has 19 episodes")]
    [InlineData(25, 19, "Only 19 of its 25 episodes are rated")]
    [InlineData(25, 20, null)]
    public void Unavailable(int episodes, int rated, string? reason)
    {
        var show = Show();
        var e = Season(show, 1, First, [.. Repeat(8.0, rated), .. Repeat(null, episodes - rated)]);

        var why = EssentialCutApi.Unavailable(e, x => x.ExternalRating, NoNotes);

        if (reason is null)
        {
            Assert.Null(why);
        }
        else
        {
            Assert.StartsWith(reason, why);
        }
    }

    private static Dictionary<Guid, (bool Keep, string Why)> Decide(ShowCopy show, List<EpisodeCopy> episodes)
    {
        var aired = episodes.Where(e => e.AirsAtUtc <= Now).ToList();
        return EssentialCutApi.Decide(show, episodes, aired, e => e.ExternalRating, EssentialCutApi.Lean, Now, NoNotes);
    }

    private static EpisodeNoteCopy Note(EpisodeCopy e, string mark) =>
        new() { EpisodeId = e.EpisodeId, ShowId = e.ShowId, Mark = mark, Reason = "Filler", SourceUrl = "https://example.org" };
}

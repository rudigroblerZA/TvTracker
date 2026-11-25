using TvTracker.IntegrationEvents;
using static Analytics.UnitTests.Build;

namespace Analytics.UnitTests;

/// <summary>
/// The per-viewer insights that are worked out from loaded facts alone: the TV clock, reunion radar, calls, the waiting
/// room, the next premiere, the pace, spoiler watch and rating bias.
/// </summary>
public class ViewerInsightsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    // ---- TV clock

    [Fact]
    public void A_clock_needs_enough_watches()
    {
        var watches = Marks(Guid.NewGuid(), new DateTime(2026, 10, 1, 21, 0, 0), TvClock.MinWatches - 1);

        var clock = TvClock.Of(watches, new DateOnly(2026, 10, 1), bulkLeftOut: 2, new Dictionary<Guid, (string, string?)>());

        Assert.False(clock.Enough);
        Assert.Equal(TvClock.MinWatches - 1, clock.Episodes);
        Assert.Equal(2, clock.BulkLeftOut);
        Assert.Empty(clock.Labels);
    }

    [Fact]
    public void Late_marks_make_a_night_owl_and_back_to_back_marks_one_sitting()
    {
        var show = Guid.NewGuid();
        // Twelve 40-minute episodes marked 40 minutes apart from 23:00 UTC: each starts 20 minutes before its mark.
        var watches = Marks(show, new DateTime(2026, 10, 1, 23, 0, 0), 12);

        var clock = TvClock.Of(watches, new DateOnly(2026, 10, 1), 0, new Dictionary<Guid, (string, string?)> { [show] = ("Night show", null) });

        Assert.True(clock.Enough);
        Assert.Contains(clock.Labels, l => l.Key == "night-owl");
        Assert.Equal(1, clock.Sittings!.Count);
        Assert.Equal(12, clock.Sittings.Longest.Episodes);
        Assert.Equal("Night show", clock.Shows.Single().Title);
    }

    [Fact]
    public void A_mark_is_placed_half_its_running_time_earlier_on_the_viewers_clock()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var watch = TvClock.Placed(Guid.NewGuid(), new DateTime(2026, 11, 2, 3, 0, 0), runtimeMinutes: 60, newYork);

        // 03:00 UTC less 30 minutes is 02:30 UTC, 21:30 the evening before in New York.
        Assert.Equal(new DateTime(2026, 11, 1, 21, 30, 0), watch.Local);
        Assert.Equal(60, watch.Minutes);
    }

    private static List<TvClock.Watch> Marks(Guid show, DateTime firstUtc, int count) =>
        Enumerable.Range(0, count).Select(i => TvClock.Placed(show, firstUtc.AddMinutes(40 * i), 40, TimeZoneInfo.Utc)).ToList();

    // ---- Reunion radar

    [Fact]
    public void Two_actors_of_a_loved_show_together_elsewhere_is_a_reunion()
    {
        var loved = Show();
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var cast = new List<CastCopy> { Member(loved, ann, "Ann Actor", "Dana", 0), Member(loved, bob, "Bob Actor", "Lee", 1) };
        var credits = new[] { Credit(ann, "new-show", "Captain Moss"), Credit(bob, "new-show", "Dr. Ray"), Credit(ann, "solo-show", "Someone") }
            .ToLookup(c => c.PersonId);

        var reunions = Reunions.Find(new Dictionary<Guid, double> { [loved.ShowId] = 1 }, new HashSet<Guid> { loved.ShowId },
            new Dictionary<Guid, ShowCopy> { [loved.ShowId] = loved }, cast, credits, Now);

        var reunion = Assert.Single(reunions);
        Assert.Equal("new-show", reunion.Show.ExternalId);
        Assert.Equal(["Ann Actor", "Bob Actor"], reunion.People.Select(p => p.Name));
    }

    [Fact]
    public void The_same_character_again_is_a_universe_not_a_reunion()
    {
        var loved = Show();
        var (ann, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var cast = new List<CastCopy> { Member(loved, ann, "Ann Actor", "Frank Castle", 0), Member(loved, bob, "Bob Actor", "Lee", 1) };
        var credits = new[] { Credit(ann, "spin-off", "Frank Castle / The Punisher"), Credit(bob, "spin-off", "Someone else") }.ToLookup(c => c.PersonId);

        Assert.Empty(Reunions.Find(new Dictionary<Guid, double> { [loved.ShowId] = 1 }, new HashSet<Guid>(),
            new Dictionary<Guid, ShowCopy> { [loved.ShowId] = loved }, cast, credits, Now));
    }

    [Theory]
    [InlineData(5, false, 0, 1.0)]
    [InlineData(4, false, 0, 0.8)]
    [InlineData(3, true, 20, 0.0)]
    [InlineData(0, true, 0, 0.5)]
    [InlineData(0, false, 3, 0.5)]
    [InlineData(0, false, 2, 0.0)]
    public void Love_weights(int stars, bool followed, int watched, double weight) =>
        Assert.Equal(weight, Reunions.LoveWeight(stars, followed, watched));

    private static CastCopy Member(ShowCopy show, Guid person, string name, string character, int order) =>
        new() { ShowId = show.ShowId, PersonId = person, PersonName = name, Character = character, Order = order };

    private static PersonCastCreditCopy Credit(Guid person, string show, string character) =>
        new() { PersonId = person, ShowExternalId = show, Title = show, Character = character, Status = ShowStatus.Ended };

    // ---- Calls

    [Theory]
    [InlineData(8.0, 7.0, 7.8, true)]
    [InlineData(6.0, 7.5, 7.8, false)]
    [InlineData(8.0, 7.6, 7.8, null)]
    [InlineData(8.0, null, 7.8, null)]
    [InlineData(8.0, 7.0, null, null)]
    public void A_call_beats_the_model_when_it_lands_nearer(double rating, double? model, double? actual, bool? beat) =>
        Assert.Equal(beat, Calls.Beat(rating, model, actual));

    [Fact]
    public void A_call_is_open_then_airing_then_scored()
    {
        Assert.Equal("open", Calls.State(null, Now.AddDays(3), Now));
        Assert.Equal("open", Calls.State(null, null, Now));
        Assert.Equal("airing", Calls.State(null, Now.AddDays(-3), Now));
        Assert.Equal("scored", Calls.State(7.5, Now.AddDays(-90), Now));
    }

    [Fact]
    public void The_record_counts_only_scored_calls()
    {
        var record = Calls.Record([(8.0, 7.0, 7.8), (6.0, 7.5, 7.8), (9.0, null, 8.0), (7.0, 7.0, null)]);

        Assert.Equal(new CallRecordDto(Scored: 3, AverageMiss: 1.0, ModelAverageMiss: 0.55, BeatModel: 1, ModelWon: 1), record);
    }

    // ---- Waiting room and the next premiere

    [Fact]
    public void A_show_is_back_with_its_next_episode_then_a_dated_season_then_unknown()
    {
        var show = Show();

        Assert.Equal("episode", WaitingRoom.Of(show, null, currentSeason: 2, Now.AddDays(-7), (2, Now.AddDays(1))).Kind);
        Assert.Equal("season", WaitingRoom.Of(show, null, currentSeason: 2, Now.AddDays(-7), (3, Now.AddDays(40))).Kind);
        Assert.Equal("unknown", WaitingRoom.Of(show, null, currentSeason: 2, Now.AddDays(-7), null).Kind);
    }

    [Fact]
    public void The_waiting_room_lists_dated_returns_before_the_unknown()
    {
        var show = Show();
        var unknown = WaitingRoom.Of(show, null, 1, Now.AddDays(-7), null);
        var later = WaitingRoom.Of(show, null, 1, Now.AddDays(-7), (2, Now.AddDays(40)));
        var sooner = WaitingRoom.Of(show, null, 1, Now.AddDays(-7), (1, Now.AddDays(2)));

        Assert.Equal([sooner, later, unknown], WaitingRoom.Ordered([unknown, later, sooner]));
    }

    [Fact]
    public void An_announced_season_sooner_than_the_listed_premiere_is_the_next_premiere()
    {
        var show = Show();
        var listed = Episode(show, 3, 1, Now.AddDays(60), null);
        var announced = new AnnouncedSeason { ShowId = show.ShowId, SeasonNumber = 3, PremieresAtUtc = Now.AddDays(30) };

        Assert.Equal(new NextPremiere(3, Now.AddDays(30), NewSeason: true), NextPremiere.Of([listed], announced, Now, premieresOnly: true));
        Assert.Equal(new NextPremiere(3, Now.AddDays(60), NewSeason: true), NextPremiere.Of([listed], null, Now, premieresOnly: true));
    }

    [Fact]
    public void A_mid_season_episode_counts_only_when_not_asking_for_premieres()
    {
        var show = Show();
        var next = Episode(show, 2, 5, Now.AddDays(3), null);

        Assert.Null(NextPremiere.Of([next], null, Now, premieresOnly: true));
        Assert.Equal(new NextPremiere(2, Now.AddDays(3), NewSeason: false), NextPremiere.Of([next], null, Now, premieresOnly: false));
    }

    // ---- Pace, spoiler watch, rating bias

    [Fact]
    public void A_day_of_bulk_marking_counts_for_a_capped_day_in_the_pace()
    {
        // 8 weeks: one day of 4,000 minutes (capped at 240) and one of 80.
        var pace = CatchUp.PaceOf([(4000, 90), (80, 2)]);

        Assert.Equal(new CatchUp.Pace((240 + 80) / CatchUp.PaceWeeks, 92), pace);
    }

    [Fact]
    public void An_unseen_finale_of_a_popular_show_is_the_riskiest()
    {
        var (popular, quiet) = (Show(), Show());
        popular.Popularity = 100;
        quiet.Popularity = 10;
        var unseen = new[]
        {
            new SpoilerWatch.Recent(popular.ShowId, Guid.NewGuid(), 2, 10, Now.AddDays(-1)),
            new SpoilerWatch.Recent(quiet.ShowId, Guid.NewGuid(), 1, 4, Now.AddDays(-1))
        };

        var risks = SpoilerWatch.Of(unseen, new Dictionary<Guid, ShowCopy> { [popular.ShowId] = popular, [quiet.ShowId] = quiet },
            new Dictionary<(Guid, int), int> { [(popular.ShowId, 2)] = 10 }, new Dictionary<Guid, ShowProgress>(), Now);

        // The quiet show's ordinary episode is under the floor and isn't listed.
        var risk = Assert.Single(risks);
        Assert.Equal(popular.ShowId, risk.ShowId);
        Assert.Equal("high", risk.Level);
        Assert.Equal("finale", risk.Latest.Kind);
    }

    [Fact]
    public void Rating_above_the_provider_is_kinder_and_needs_three_to_compare()
    {
        var shows = new[] { (5, Show(rating: 7.0)), (5, Show(rating: 8.0)), (4, Show(rating: 7.0)) };

        Assert.Null(Taste.Bias(shows.Take(2).ToList(), []));
        var bias = Taste.Bias(shows, [])!;
        Assert.Equal("kinder", bias.Label);
        Assert.Equal(3, bias.Compared);
        Assert.Equal(2.0, bias.AverageDifference);
    }
}

using Analytics.API.Apis;
using TvTracker.IntegrationEvents;

namespace Analytics.UnitTests;

/// <summary>
/// While you wait: a return in view, a budget from the pace, picks that fit and resemble the awaited show, each picked once
/// (see CONTEXT.md, While you wait).
/// </summary>
public class WhileYouWaitTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    [Theory]
    [InlineData("season", 30, false, true)]
    [InlineData("episode", 7, false, true)]
    [InlineData("season", 6, false, false)]
    [InlineData("season", 181, false, false)]
    [InlineData("forecast", 60, false, true)]
    [InlineData("forecast", 60, true, false)]
    [InlineData("unknown", 60, false, false)]
    public void A_return_in_view_is_between_a_week_and_six_months_away(string kind, int days, bool overdue, bool inView)
    {
        var back = Today.AddDays(days);
        var at = kind is "episode" or "season" ? new DateTimeOffset(back.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
        var likely = kind == "forecast" ? back : (DateOnly?)null;
        var show = new WaitingShowDto(Guid.NewGuid(), "Severance", null, null, kind, at, 3, likely, null, null, overdue, null, null);

        Assert.Equal(inView, WhileYouWait.Awaited(show, Today, TimeZoneInfo.Utc) is not null);
    }

    [Fact]
    public void A_premiere_is_dated_in_the_viewers_calendar()
    {
        // Sunday 21:00 in New York is 02:00 UTC on Monday; the viewer sees Sunday.
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var show = new WaitingShowDto(Guid.NewGuid(), "x", null, null, "season", new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero), 2,
            null, null, null, false, null, null);

        var wait = WhileYouWait.Awaited(show, Today, newYork);

        Assert.Equal(new DateOnly(2026, 11, 15), wait!.BackAt);
    }

    [Fact]
    public void A_pick_that_fills_the_budget_still_finishes_in_time()
    {
        // 10 days at 100 minutes a week is 142.86 minutes: a 143-minute show would finish a day late, so the budget is 142.
        var waits = new List<WhileYouWait.Wait> { new(Guid.NewGuid(), "x", null, "season", Today.AddDays(10), 2) };
        var candidates = new List<TimeOffApi.Candidate> { Candidate("Short", 143, ["Drama"], null, 8.5), Candidate("Shorter", 140, ["Drama"], null, 8.5) };

        var plan = WhileYouWait.Plan(waits, candidates, pace: 100, Today, _ => ["Drama"], _ => null);

        Assert.Equal(142, plan[0].BudgetMinutes);
        var pick = Assert.Single(plan[0].Picks);
        Assert.Equal(("Shorter", 0), (pick.Title, pick.DaysToSpare));
    }

    [Fact]
    public void Picks_fit_the_budget_and_resemble_the_awaited_show_and_are_used_once()
    {
        var severance = Guid.NewGuid();
        var silo = Guid.NewGuid();
        var waits = new List<WhileYouWait.Wait>
        {
            new(severance, "Severance", null, "season", Today.AddDays(28), 3), // 4 weeks × 10 h = 40 h
            new(silo, "Silo", null, "season", Today.AddDays(56), 3)           // 8 weeks = 80 h
        };
        var mystery = Dna((DnaTraits.Release, 0.0), (DnaTraits.Acclaim, 0.9), (DnaTraits.EpisodeLength, 1.0), (DnaTraits.SeasonSize, 0.2), (DnaTraits.Finales, 0.9));
        var candidates = new List<TimeOffApi.Candidate>
        {
            Candidate("Dark", 26 * 60, ["Drama", "Mystery", "Science-Fiction"], mystery, 8.7),            // 26 h, alike, fits the first
            Candidate("Counterpart", 20 * 60, ["Drama", "Science-Fiction", "Thriller"], mystery, 8.1),   // 20 h, alike, fits the first
            Candidate("Friends", 85 * 60, ["Comedy"], Dna((DnaTraits.Release, 0.0), (DnaTraits.Acclaim, 0.6), (DnaTraits.EpisodeLength, 0.0), (DnaTraits.SeasonSize, 0.9), (DnaTraits.Finales, 0.4)), 8.5), // too long for either
            Candidate("The Office", 70 * 60, ["Comedy"], null, 8.6),                                    // fits only the second, unlike
            Candidate("Filler", 10 * 60, ["Comedy"], null, 7.2)                                         // fits both, unlike and not good enough
        };

        var plan = WhileYouWait.Plan(waits, candidates, pace: 600, Today,
            _ => ["Drama", "Mystery", "Science-Fiction", "Thriller"], _ => mystery);

        // The taste line would repeat the genres Alike named, so only Alike's line shows; the rating is the pill.
        Assert.DoesNotContain(plan[0].Picks[0].Reasons, r => r.StartsWith("Your kind of show") || r.StartsWith("Rated"));

        Assert.Equal(["Severance", "Silo"], plan.Select(w => w.Title));
        Assert.Equal(2400, plan[0].BudgetMinutes);
        // Dark and Counterpart fit Severance's four weeks and are alike; Friends and The Office don't fit.
        Assert.Equal(["Dark", "Counterpart"], plan[0].Picks.Select(p => p.Title));
        Assert.All(plan[0].Picks, p => Assert.True(p.Alike >= 0.5));
        Assert.Contains("Also drama, mystery, science-fiction", plan[0].Picks[0].Reasons);
        // 26 h at 10 h a week is 19 days: done 9 days before the premiere.
        Assert.Equal((Today.AddDays(19), 9), (plan[0].Picks[0].FinishesAround, plan[0].Picks[0].DaysToSpare));
        // Silo's 80 hours fit The Office (not picked already) but not Friends; Dark and Counterpart are already taken, and a
        // 7.2 that's nothing like Silo isn't worth offering as a change of pace.
        Assert.Equal(["The Office"], plan[1].Picks.Select(p => p.Title));
        Assert.Contains("A change of pace", plan[1].Picks[0].Reasons);
    }

    [Fact]
    public void Alike_stands_on_genres_alone_when_dna_is_missing()
    {
        var candidate = Candidate("x", 600, ["Drama", "Crime"], null, 8).Show;

        var likeness = WhileYouWait.Alike(["Drama", "Crime"], null, candidate);

        Assert.Equal(1, likeness.Alike);
        Assert.Equal(["Also drama, crime"], likeness.Reasons);
        Assert.True(likeness.NamedGenres);
    }

    private static IReadOnlyDictionary<string, double> Dna(params (string Key, double Value)[] traits) => traits.ToDictionary(t => t.Key, t => t.Value);

    private static TimeOffApi.Candidate Candidate(string title, int minutes, string[] genres, IReadOnlyDictionary<string, double>? dna, double rating)
    {
        var show = new TimeOffApi.ShelfShow(Guid.NewGuid(), title, null, rating, genres, minutes / 50, minutes, null, null, false, dna);
        return new TimeOffApi.Candidate(show, ShowValue.Assess(rating, genres, null, null, false, ["Drama", "Mystery"]));
    }
}

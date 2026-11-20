using Analytics.API.Apis;
using Analytics.Data;
using TvTracker.IntegrationEvents;

namespace Analytics.IntegrationTests;

public sealed class TimeMachineQueryTests(AnalyticsDatabase database) : IClassFixture<AnalyticsDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private async Task<Guid> ShowAsync(ShowStatus status = ShowStatus.Ended)
    {
        await using var db = database.Context();
        var show = new ShowCopy { ShowId = Guid.NewGuid(), Title = $"Show {Guid.NewGuid():N}", Status = status, Popularity = 1, ExternalRating = 8 };
        db.Shows.Add(show);
        await db.SaveChangesAsync(Ct);
        return show.ShowId;
    }

    private async Task<Guid> EpisodeAsync(Guid showId, DateTimeOffset? airs, int season = 1, int number = 1, double? rating = 8)
    {
        await using var db = database.Context();
        var episode = new EpisodeCopy
        {
            EpisodeId = Guid.NewGuid(),
            ShowId = showId,
            SeasonNumber = season,
            EpisodeNumber = number,
            Title = $"Ep {number}",
            AirsAtUtc = airs,
            ExternalRating = rating
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync(Ct);
        return episode.EpisodeId;
    }

    private async Task<List<Guid>> InWeeksAsync(TimeZoneInfo zone, int halfWidth, params DateOnly[] middles)
    {
        await using var db = database.Context();
        return (await TimeMachineApi.InWeeksAsync(db, zone, [.. middles], halfWidth, Ct)).Select(e => e.EpisodeId).ToList();
    }

    [Fact]
    public async Task Episodes_within_the_half_width_of_a_middle_day_are_found_and_others_are_not()
    {
        var show = await ShowAsync();
        var middle = new DateOnly(1999, 3, 10);
        var inside = await EpisodeAsync(show, new DateTimeOffset(1999, 3, 7, 12, 0, 0, TimeSpan.Zero), number: 1);
        var lastDay = await EpisodeAsync(show, new DateTimeOffset(1999, 3, 13, 23, 59, 0, TimeSpan.Zero), number: 2);
        var tooEarly = await EpisodeAsync(show, new DateTimeOffset(1999, 3, 6, 23, 59, 0, TimeSpan.Zero), number: 3);
        var tooLate = await EpisodeAsync(show, new DateTimeOffset(1999, 3, 14, 0, 0, 0, TimeSpan.Zero), number: 4);

        var found = await InWeeksAsync(Utc, 3, middle);

        Assert.Contains(inside, found);
        Assert.Contains(lastDay, found);
        Assert.DoesNotContain(tooEarly, found);
        Assert.DoesNotContain(tooLate, found);
    }

    [Fact]
    public async Task Several_weeks_are_scanned_in_one_query_and_each_episode_is_returned_once()
    {
        var show = await ShowAsync();
        var a = await EpisodeAsync(show, new DateTimeOffset(1985, 6, 1, 20, 0, 0, TimeSpan.Zero), number: 1);
        var b = await EpisodeAsync(show, new DateTimeOffset(2001, 6, 2, 20, 0, 0, TimeSpan.Zero), number: 2);
        var between = await EpisodeAsync(show, new DateTimeOffset(1993, 6, 1, 20, 0, 0, TimeSpan.Zero), number: 3);

        var found = await InWeeksAsync(Utc, 3, new DateOnly(1985, 6, 1), new DateOnly(2001, 6, 1));

        Assert.Contains(a, found);
        Assert.Contains(b, found);
        Assert.DoesNotContain(between, found);
        Assert.Equal(found.Distinct().Count(), found.Count);
    }

    [Fact]
    public async Task Specials_and_episodes_with_no_air_time_are_left_out()
    {
        var show = await ShowAsync();
        var special = await EpisodeAsync(show, new DateTimeOffset(1977, 5, 5, 12, 0, 0, TimeSpan.Zero), season: 0, number: 1);
        var unscheduled = await EpisodeAsync(show, null, number: 2);
        var regular = await EpisodeAsync(show, new DateTimeOffset(1977, 5, 5, 12, 0, 0, TimeSpan.Zero), number: 3);

        var found = await InWeeksAsync(Utc, 3, new DateOnly(1977, 5, 5));

        Assert.Contains(regular, found);
        Assert.DoesNotContain(special, found);
        Assert.DoesNotContain(unscheduled, found);
    }

    [Fact]
    public async Task Days_are_the_callers_calendar_days_not_UTC()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var show = await ShowAsync();
        // 02:00 UTC on 11 March 1990 is the evening of the 10th in New York (EST, UTC-5).
        var evening = await EpisodeAsync(show, new DateTimeOffset(1990, 3, 11, 2, 0, 0, TimeSpan.Zero), number: 1);

        // A zero-width week on the 11th (New York) starts at 05:00 UTC, so the evening one is before it.
        Assert.DoesNotContain(evening, await InWeeksAsync(newYork, 0, new DateOnly(1990, 3, 11)));
        Assert.Contains(evening, await InWeeksAsync(newYork, 0, new DateOnly(1990, 3, 10)));
    }

    [Fact]
    public async Task A_premiere_and_an_ended_shows_finale_are_found_for_one_date()
    {
        var show = await ShowAsync(ShowStatus.Ended);
        var premiere = await EpisodeAsync(show, new DateTimeOffset(1996, 9, 3, 20, 0, 0, TimeSpan.Zero), season: 1, number: 1);
        await EpisodeAsync(show, new DateTimeOffset(1996, 12, 3, 20, 0, 0, TimeSpan.Zero), season: 1, number: 2);
        var finale = await EpisodeAsync(show, new DateTimeOffset(1997, 5, 6, 20, 0, 0, TimeSpan.Zero), season: 1, number: 3);

        await using var db = database.Context();
        var premiereWeek = await TimeMachineApi.ComputeAsync(db, Utc, new DateOnly(2026, 1, 1), new DateOnly(1996, 9, 3), Ct);
        var finaleWeek = await TimeMachineApi.ComputeAsync(db, Utc, new DateOnly(2026, 1, 1), new DateOnly(1997, 5, 6), Ct);

        Assert.Contains(premiereWeek.Premieres, e => e.EpisodeId == premiere);
        Assert.DoesNotContain(premiereWeek.Finales, e => e.EpisodeId == premiere);
        Assert.Contains(finaleWeek.Finales, e => e.EpisodeId == finale);
        Assert.Contains(premiereWeek.OnAir, s => s.ShowId == show);
    }
}

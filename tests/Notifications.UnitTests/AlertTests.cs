using Notifications.API.Data;
using Notifications.API.IntegrationEvents;
using Notifications.API.Services;
using TvTracker.IntegrationEvents;

namespace Notifications.UnitTests;

/// <summary>
/// Which preference each kind of alert follows, and what season verdicts and crossovers say.
/// </summary>
public class AlertTests
{
    [Fact]
    public void Verdicts_have_their_own_switch_and_crossovers_follow_new_episodes()
    {
        var noSeasons = new NotificationPreferences { UserId = "u", NewSeasons = false };
        var noVerdicts = new NotificationPreferences { UserId = "u", SeasonVerdicts = false };
        var noEpisodes = new NotificationPreferences { UserId = "u", NewEpisodes = false };

        // Turning verdicts off stops them without stopping new seasons, and the other way round.
        Assert.False(NotificationFanOut.Wants(noVerdicts, NotificationKind.SeasonVerdict));
        Assert.True(NotificationFanOut.Wants(noVerdicts, NotificationKind.NewSeason));
        Assert.True(NotificationFanOut.Wants(noSeasons, NotificationKind.SeasonVerdict));
        Assert.False(NotificationFanOut.Wants(noSeasons, NotificationKind.NewSeason));

        Assert.True(NotificationFanOut.Wants(noSeasons, NotificationKind.Crossover));
        Assert.False(NotificationFanOut.Wants(noEpisodes, NotificationKind.Crossover));
        Assert.True(NotificationFanOut.Wants(noEpisodes, NotificationKind.SeasonVerdict));
    }

    [Fact]
    public void A_season_verdict_reads_as_its_doc_comment_says()
    {
        var verdict = new SeasonVerdictIntegrationEvent(Guid.NewGuid(), "Severance", 2, SeasonVerdicts.BestYet, 8.6, 8.1,
            FinaleVerdicts.Strong, 9.1, 20, 12);

        Assert.Equal("Its best season yet, rated 8.6 against 8.1 for the others. The finale was a high point. " +
            "12 of the 20 viewers here who started it have finished it.", AnalyticsNotificationHandlers.SeasonVerdictText(verdict));
    }

    [Fact]
    public void A_season_verdict_without_viewers_or_finale_says_only_the_rating()
    {
        var verdict = new SeasonVerdictIntegrationEvent(Guid.NewGuid(), "Silo", 1, SeasonVerdicts.FirstSeason, 8.04, null,
            null, null, null, null);

        Assert.Equal("Its first season rated 8.0.", AnalyticsNotificationHandlers.SeasonVerdictText(verdict));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    private static CrossoverScheduledIntegrationEvent Crossover(TimeSpan ahead, params string[] shows) => new(
        Guid.NewGuid(), "Infection", Guid.NewGuid(), "One Chicago",
        shows.Select((s, i) => new CrossoverPartItem(Guid.NewGuid(), s, Guid.NewGuid(), 13, 4, Now + ahead + TimeSpan.FromHours(i))).ToList());

    [Fact]
    public void A_crossover_names_its_shows_and_how_many_days_away_it_starts()
    {
        Assert.Equal("Airs across Chicago Fire, Chicago Med and Chicago P.D., starting in 6 days. " +
            "Catch up first: the watch order has every episode in sequence.",
            CrossoverNotificationHandler.CrossoverText(Crossover(TimeSpan.FromDays(5.2), "Chicago Fire", "Chicago Med", "Chicago P.D."), Now));
    }

    [Fact]
    public void A_crossover_soon_says_tomorrow_or_today()
    {
        Assert.StartsWith("Airs across Chicago Fire and Chicago Med, starting tomorrow.",
            CrossoverNotificationHandler.CrossoverText(Crossover(TimeSpan.FromHours(20), "Chicago Fire", "Chicago Med"), Now));
        // Two parts on one show (a two-parter) name it once.
        Assert.StartsWith("Airs across Chicago Fire, starting today.",
            CrossoverNotificationHandler.CrossoverText(Crossover(TimeSpan.FromHours(-1), "Chicago Fire", "Chicago Fire"), Now));
    }
}

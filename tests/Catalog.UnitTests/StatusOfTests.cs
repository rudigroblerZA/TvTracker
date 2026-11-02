using Catalog.SyncWorker;
using Catalog.SyncWorker.Providers;
using TvTracker.IntegrationEvents;
using static Catalog.UnitTests.CatalogOverridesTests;

namespace Catalog.UnitTests;

/// <summary>A show the provider still calls running after four quiet years is presumed ended (see CONTEXT.md, Presumed ended).</summary>
public class StatusOfTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Quiet_for_four_years_is_presumed_ended() =>
        Assert.Equal(ShowStatus.Ended, CatalogSynchronizer.StatusOf(Show(Episode("e1", 6, 10, "Finale", Now.AddYears(-5))), Now));

    [Fact]
    public void An_undated_empty_season_does_not_count_as_coming() =>
        Assert.Equal(ShowStatus.Ended, CatalogSynchronizer.StatusOf(
            Show(ShowStatus.Airing, [new ProviderSeason(7, "Season 7", null, null)], Episode("e1", 6, 10, "Finale", Now.AddYears(-5))), Now));

    [Fact]
    public void Recently_aired_stays_airing() =>
        Assert.Equal(ShowStatus.Airing, CatalogSynchronizer.StatusOf(Show(Episode("e1", 2, 8, "Finale", Now.AddYears(-3))), Now));

    [Fact]
    public void A_scheduled_or_TBA_episode_keeps_it_airing()
    {
        Assert.Equal(ShowStatus.Airing, CatalogSynchronizer.StatusOf(
            Show(Episode("e1", 1, 8, "Finale", Now.AddYears(-6)), Episode("e2", 2, 1, "TBA", Now.AddDays(30))), Now));
        Assert.Equal(ShowStatus.Airing, CatalogSynchronizer.StatusOf(
            Show(Episode("e1", 1, 8, "Finale", Now.AddYears(-6)), Episode("e2", 2, 1, "TBA", null)), Now));
    }

    [Fact]
    public void A_dated_season_keeps_it_airing() =>
        Assert.Equal(ShowStatus.Airing, CatalogSynchronizer.StatusOf(
            Show(ShowStatus.Airing, [new ProviderSeason(2, "Season 2", null, Now.AddMonths(3))], Episode("e1", 1, 8, "Finale", Now.AddYears(-6))), Now));

    [Fact]
    public void Other_statuses_pass_through() =>
        Assert.Equal(ShowStatus.Ended, CatalogSynchronizer.StatusOf(Show(ShowStatus.Ended, [], Episode("e1", 1, 8, "Finale", Now.AddDays(-2))), Now));
}

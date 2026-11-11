using Catalog.Data;
using Catalog.SyncWorker.Providers;
using Catalog.SyncWorker.Supplement;
using Microsoft.Extensions.Time.Testing;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.UnitTests;

/// <summary>
/// Wikidata's values go into a show only where the provider has none, and a job that sleeps between rounds can be
/// asked to run now.
/// </summary>
public class CatalogSupplementsTests
{
    private static ProviderShow Show(string? overview = null, string? network = null, DateOnly? firstAired = null, string? poster = null) =>
        new("1", "Show", overview, poster, null, firstAired, ShowStatus.Airing, network, [], [], 0, [], [], [], [], [], null);

    private static readonly ShowSupplement Wikidata = new()
    {
        Item = "Q1", Overview = "From Wikipedia.", Network = "BBC One", FirstAirDate = new DateOnly(2020, 1, 2), PosterUrl = "https://commons/p.jpg"
    };

    [Fact]
    public void Empty_fields_are_filled()
    {
        var filled = CatalogSupplements.Fill(Show(overview: " ", network: ""), Wikidata);

        Assert.Equal("From Wikipedia.", filled.Overview);
        Assert.Equal("BBC One", filled.Network);
        Assert.Equal(new DateOnly(2020, 1, 2), filled.FirstAirDate);
        Assert.Equal("https://commons/p.jpg", filled.PosterUrl);
    }

    [Fact]
    public void What_the_provider_has_is_never_replaced()
    {
        var show = Show("The provider's.", "HBO", new DateOnly(2019, 5, 6), "https://provider/p.jpg");

        Assert.Equal(show, CatalogSupplements.Fill(show, Wikidata));
    }

    [Fact]
    public void Without_a_supplement_or_with_an_empty_one_the_show_is_as_it_was()
    {
        var show = Show();

        Assert.Same(show, CatalogSupplements.Fill(show, null));
        Assert.Equal(show, CatalogSupplements.Fill(show, new ShowSupplement()));
    }

    [Fact]
    public void A_supplement_helps_only_where_the_show_lacks_what_it_has()
    {
        Assert.True(CatalogSupplements.Helps(Wikidata, "Has one.", null, new DateOnly(2019, 1, 1), "p.jpg"));
        Assert.False(CatalogSupplements.Helps(Wikidata, "Has one.", "HBO", new DateOnly(2019, 1, 1), "p.jpg"));
        // Wikidata found the show but has no network either.
        Assert.False(CatalogSupplements.Helps(new ShowSupplement { Item = "Q1" }, "Has one.", null, new DateOnly(2019, 1, 1), "p.jpg"));
    }

    [Theory]
    [InlineData(CatalogSources.TvMaze, "P8600")]
    [InlineData(CatalogSources.Tvdb, "P4835")]
    [InlineData(CatalogSources.Wikidata, null)]
    [InlineData(CatalogSources.Mock, null)]
    public void Shows_are_found_on_Wikidata_by_their_providers_id(string source, string? property) =>
        Assert.Equal(property, CatalogSupplements.IdProperty(source));

    [Fact]
    public async Task A_sleeping_job_wakes_when_asked_to_run_and_stays_asked_until_nothing_is_due()
    {
        var time = new FakeTimeProvider();
        var run = new JobRun("atlas");
        var sleeping = run.SleepAsync(TimeSpan.FromHours(6), time, TestContext.Current.CancellationToken);
        Assert.False(sleeping.IsCompleted);

        Assert.True(run.Request());
        await sleeping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        run.RoundStarted();
        Assert.Equal("running", run.Status.State);
        run.RoundEnded(25, 40, time.GetUtcNow().UtcDateTime);
        Assert.True(run.Requested);
        // Still asked: no sleep between batches.
        Assert.True(run.SleepAsync(TimeSpan.FromHours(6), time, TestContext.Current.CancellationToken).IsCompleted);

        run.RoundEnded(15, 0, time.GetUtcNow().UtcDateTime);
        var status = run.Status;
        Assert.False(status.Requested);
        Assert.Equal(("idle", 40, 0), (status.State, status.Done, status.Due));
    }

    [Fact]
    public async Task Unasked_a_job_sleeps_its_time_and_a_failed_round_ends_the_request()
    {
        var time = new FakeTimeProvider();
        var run = new JobRun("fill");
        var sleeping = run.SleepAsync(TimeSpan.FromMinutes(2), time, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await sleeping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        run.Request();
        run.RoundEnded(0, null, time.GetUtcNow().UtcDateTime, "Wikidata is busy");
        Assert.False(run.Requested);
        Assert.Equal("Wikidata is busy", run.Status.LastError);
    }

    [Fact]
    public void A_job_thats_off_cant_be_asked_to_run()
    {
        var run = new JobRun("atlas");
        run.TurnOff("Off: the catalog isn't from TVmaze.");

        Assert.False(run.Request());
        Assert.Equal(("off", "Off: the catalog isn't from TVmaze."), (run.Status.State, run.Status.Note));
    }
}

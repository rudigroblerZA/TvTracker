using Catalog.Data;
using Catalog.SyncWorker;
using Catalog.SyncWorker.JustWatch;
using Catalog.SyncWorker.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;
using TvTracker.Tests;

namespace Catalog.IntegrationTests;

/// <summary>
/// Show sync through its interface: a provider's show goes in by <see cref="CatalogSynchronizer.ImportShowAsync"/> or
/// <see cref="CatalogSynchronizer.RefreshAsync"/>, and what's asserted is what the catalog holds afterwards and what
/// the other services are told. The provider is scripted; the database is real.
/// </summary>
public sealed class CatalogSynchronizerTests(CatalogDatabase database) : IClassFixture<CatalogDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly HybridCache Cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();

    private readonly FakeTimeProvider time = new(Now);
    private readonly RecordingEventBus bus = new();
    private readonly ScriptedCatalogProvider provider = new();

    /// <summary>
    /// Tests share a database: each one's show ids and people's names carry this, so they never meet.
    /// </summary>
    private readonly string tag = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Importing ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Importing_stores_the_show_and_tells_of_it_before_its_episodes()
    {
        var show = Offer(Show("Severance", episodes: [Episode(1, 1, "Good News About Hell", Now.AddDays(-7)), Episode(1, 2, "Half Loop", Now.AddDays(7))]));

        var result = await SyncAsync(s => s.ImportShowAsync(show.ExternalId, Ct));

        Assert.Equal(new ImportResult(ImportOutcome.Imported, "Severance"), result);
        var stored = await StoredAsync(show);
        Assert.Equal(("Severance", ShowStatus.Airing, "Apple TV+"), (stored.Title, stored.Status, stored.Network));
        Assert.Equal([1], stored.Seasons.Select(s => s.Number));
        Assert.Equal(["Good News About Hell", "Half Loop"], stored.Episodes.OrderBy(e => e.EpisodeNumber).Select(e => e.Title));
        // One that had aired when it was imported never raises a "new episode" alert; one still to come will.
        Assert.Equal([true, false], stored.Episodes.OrderBy(e => e.EpisodeNumber).Select(e => e.AiredPublished));
        // Due again in three days: an episode aired this week, and its rating is still settling.
        Assert.Equal(Now.UtcDateTime.AddDays(3), stored.NextRefreshAtUtc);

        Assert.Collection(bus.Published,
            first => Assert.Equal(stored.Id, Assert.IsType<ShowAddedIntegrationEvent>(first).ShowId),
            second => Assert.Equal(2, Assert.IsType<EpisodesScheduledIntegrationEvent>(second).Episodes.Count));
    }

    [Fact]
    public async Task Importing_a_show_the_catalog_has_does_nothing()
    {
        var show = Offer(Show("Slow Horses"));
        await SyncAsync(s => s.ImportShowAsync(show.ExternalId, Ct));
        bus.Clear();

        var again = await SyncAsync(s => s.ImportShowAsync(show.ExternalId, Ct));

        Assert.Equal(new ImportResult(ImportOutcome.AlreadyInCatalog, "Slow Horses"), again);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task Importing_an_id_the_provider_doesnt_have_finds_nothing()
    {
        var result = await SyncAsync(s => s.ImportShowAsync($"missing-{tag}", Ct));

        Assert.Equal(ImportOutcome.NotFound, result.Outcome);
        Assert.Empty(bus.Published);
    }

    // --- Refreshing ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_that_finds_nothing_new_says_nothing()
    {
        var show = await ImportedAsync(Show("Andor"));

        await SyncAsync(s => s.RefreshAsync(show, Ct));

        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task A_changed_detail_is_stored_and_told_once()
    {
        var show = await ImportedAsync(Show("The Bear"));

        await SyncAsync(s => s.RefreshAsync(show with { Overview = "A chef comes home to Chicago." }, Ct));

        Assert.Equal("A chef comes home to Chicago.", (await StoredAsync(show)).Overview);
        Assert.Equal("The Bear", Assert.IsType<ShowUpdatedIntegrationEvent>(Assert.Single(bus.Published)).Title);
    }

    [Fact]
    public async Task Only_the_episode_that_moved_is_scheduled_again_and_it_will_alert_when_it_airs()
    {
        var show = await ImportedAsync(Show("Shrinking", episodes: [Episode(1, 1, "Coin Flip", Now.AddDays(-2)), Episode(1, 2, "Fortress of Solitude", Now.AddDays(5))]));
        var first = show.Episodes[0];

        // The provider had the date wrong: it hasn't aired after all.
        await SyncAsync(s => s.RefreshAsync(show with { Episodes = [first with { AirsAtUtc = Now.AddDays(3) }, show.Episodes[1]] }, Ct));

        var moved = (await StoredAsync(show)).Episodes.Single(e => e.EpisodeNumber == 1);
        Assert.Equal((Now.AddDays(3), false), (moved.AirsAtUtc, moved.AiredPublished));
        var scheduled = Assert.IsType<EpisodesScheduledIntegrationEvent>(Assert.Single(bus.Published));
        Assert.Equal(moved.Id, Assert.Single(scheduled.Episodes).EpisodeId);
    }

    [Fact]
    public async Task A_new_season_is_announced()
    {
        var show = await ImportedAsync(Show("Silo"));
        var premiere = Now.AddMonths(4);

        await SyncAsync(s => s.RefreshAsync(show with
        {
            Seasons = [.. show.Seasons, new ProviderSeason(2, "Season 2", null, premiere)],
            Episodes = [.. show.Episodes, Episode(2, 1, "The Engineer", premiere)]
        }, Ct));

        Assert.Equal([1, 2], (await StoredAsync(show)).Seasons.Select(s => s.Number).Order());
        var announced = Assert.Single(bus.Of<SeasonAnnouncedIntegrationEvent>());
        Assert.Equal(("Silo", 2, (DateTimeOffset?)premiere), (announced.ShowTitle, announced.SeasonNumber, announced.PremieresAtUtc));
        Assert.Equal("The Engineer", Assert.Single(Assert.Single(bus.Of<EpisodesScheduledIntegrationEvent>()).Episodes).Title);
    }

    [Fact]
    public async Task A_show_that_ends_is_told_with_what_it_was()
    {
        var show = await ImportedAsync(Show("Succession"));

        await SyncAsync(s => s.RefreshAsync(show with { Status = ShowStatus.Ended }, Ct));

        var stored = await StoredAsync(show);
        Assert.Equal(ShowStatus.Ended, stored.Status);
        // An ended show is looked at again in a month, not a week.
        Assert.Equal(Now.UtcDateTime.AddDays(30), stored.NextRefreshAtUtc);
        var changed = Assert.Single(bus.Of<ShowStatusChangedIntegrationEvent>());
        Assert.Equal((ShowStatus.Airing, ShowStatus.Ended), (changed.OldStatus, changed.NewStatus));
    }

    [Fact]
    public async Task A_show_the_provider_calls_airing_with_nothing_for_years_is_stored_as_ended()
    {
        var show = Offer(Show("Forgotten", episodes: [Episode(1, 1, "Pilot", Now.AddYears(-6))]));

        await SyncAsync(s => s.ImportShowAsync(show.ExternalId, Ct));

        Assert.Equal(ShowStatus.Ended, (await StoredAsync(show)).Status);
    }

    // --- Corrections and gap-filling ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_correction_is_told_at_once_survives_refreshes_and_goes_when_removed()
    {
        var show = await ImportedAsync(Show("Dark", episodes: [Episode(1, 1, "Secrets", Now.AddDays(10))]));
        var episode = (await StoredAsync(show)).Episodes.Single();
        var correction = new CatalogOverride { Id = Guid.NewGuid(), ShowId = episode.ShowId, EpisodeId = episode.Id, Field = CatalogOverrideFields.EpisodeTitle, Value = "Geheimnisse" };

        Assert.True(await SyncAsync(s => s.AddOverrideAsync(correction, Ct)));

        Assert.Equal("Geheimnisse", Assert.Single(Assert.IsType<EpisodesScheduledIntegrationEvent>(Assert.Single(bus.Published)).Episodes).Title);
        bus.Clear();

        // The provider still says what it said: the correction holds, and nothing changed to tell anyone.
        await SyncAsync(s => s.RefreshAsync(show, Ct));
        Assert.Equal("Geheimnisse", (await StoredAsync(show)).Episodes.Single().Title);
        Assert.Empty(bus.Published);

        Assert.True(await SyncAsync(s => s.RemoveOverrideAsync(correction.Id, Ct)));
        // Removing it makes the show due now, and that refresh brings the provider's value back.
        Assert.Equal(Now.UtcDateTime, (await StoredAsync(show)).NextRefreshAtUtc);
        await SyncAsync(s => s.RefreshAsync(show, Ct));
        Assert.Equal("Secrets", (await StoredAsync(show)).Episodes.Single().Title);
        Assert.Equal("Secrets", Assert.Single(Assert.IsType<EpisodesScheduledIntegrationEvent>(Assert.Single(bus.Published)).Episodes).Title);
    }

    [Fact]
    public async Task A_correction_to_a_show_or_episode_the_catalog_lacks_is_turned_away()
    {
        var show = await ImportedAsync(Show("Dept. Q"));
        var showId = (await StoredAsync(show)).Id;

        Assert.False(await SyncAsync(s => s.AddOverrideAsync(
            new CatalogOverride { Id = Guid.NewGuid(), ShowId = Guid.NewGuid(), Field = CatalogOverrideFields.ShowSummary, Value = "x" }, Ct)));
        Assert.False(await SyncAsync(s => s.AddOverrideAsync(
            new CatalogOverride { Id = Guid.NewGuid(), ShowId = showId, EpisodeId = Guid.NewGuid(), Field = CatalogOverrideFields.EpisodeTitle, Value = "x" }, Ct)));
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task What_the_provider_leaves_empty_is_filled_from_the_supplement_and_what_it_has_is_kept()
    {
        var show = await ImportedAsync(Show("Babylon Berlin") with { Overview = null });
        var showId = (await StoredAsync(show)).Id;
        await using (var db = database.Context())
        {
            db.Supplements.Add(new ShowSupplement { ShowId = showId, Overview = "Berlin, 1929.", Network = "Das Erste", CheckedAtUtc = Now.UtcDateTime });
            await db.SaveChangesAsync(Ct);
        }

        await SyncAsync(s => s.RefreshAsync(show with { Overview = null }, Ct));

        var stored = await StoredAsync(show);
        Assert.Equal("Berlin, 1929.", stored.Overview);
        // The provider names a network, so the supplement's isn't used.
        Assert.Equal("Apple TV+", stored.Network);
        Assert.IsType<ShowUpdatedIntegrationEvent>(Assert.Single(bus.Published));
    }

    // --- People -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Someone_in_two_shows_is_one_person()
    {
        var actor = new ProviderPerson($"p-{tag}", new DateOnly(1971, 4, 18), null, "Male", "GB");
        var first = Offer(Show("Broadchurch") with { Cast = [new ProviderCastMember(Name("David Tennant"), "Alec Hardy", null, [], actor)] });
        var second = Offer(Show("Good Omens") with { Cast = [new ProviderCastMember(Name("David Tennant"), "Crowley", null, [], actor)] });

        await SyncAsync(s => s.ImportShowAsync(first.ExternalId, Ct));
        await SyncAsync(s => s.ImportShowAsync(second.ExternalId, Ct));

        await using var db = database.Context();
        var person = await db.People.SingleAsync(p => p.Name == Name("David Tennant"), Ct);
        Assert.Equal((actor.ExternalId, actor.Birthday), (person.ExternalId, person.Birthday));
        Assert.Equal(["Alec Hardy", "Crowley"], await db.Cast.Where(c => c.PersonId == person.Id).Select(c => c.Character!).OrderBy(c => c).ToListAsync(Ct));
    }

    [Fact]
    public async Task Someone_first_known_only_by_name_gains_the_providers_id_instead_of_a_double()
    {
        var byName = Offer(Show("Fleabag") with { Cast = [new ProviderCastMember(Name("Olivia Colman"), "Godmother", null, [])] });
        await SyncAsync(s => s.ImportShowAsync(byName.ExternalId, Ct));
        var withId = Offer(Show("The Crown") with { Cast = [new ProviderCastMember(Name("Olivia Colman"), "Elizabeth II", null, [], new ProviderPerson($"p-{tag}", null, null, null, null))] });

        await SyncAsync(s => s.ImportShowAsync(withId.ExternalId, Ct));

        await using var db = database.Context();
        Assert.Equal($"p-{tag}", (await db.People.SingleAsync(p => p.Name == Name("Olivia Colman"), Ct)).ExternalId);
    }

    [Fact]
    public async Task A_refresh_that_couldnt_fetch_the_cast_keeps_the_cast_it_had()
    {
        var show = await ImportedAsync(Show("Poker Face") with { Cast = [new ProviderCastMember(Name("Natasha Lyonne"), "Charlie Cale", null, [])] });

        await SyncAsync(s => s.RefreshAsync(show with { Cast = [], CastMissing = true }, Ct));

        Assert.Equal("Charlie Cale", Assert.Single((await StoredAsync(show)).Cast).Character);
        Assert.Empty(bus.Published);
    }

    // --- Where to watch, with JustWatch ------------------------------------------------------------------------------

    [Fact]
    public async Task A_country_JustWatch_knows_the_show_in_keeps_its_rows_through_a_refresh()
    {
        var guess = new[] { new ProviderWatchOption("US", "Apple TV", WatchProviderType.Stream, "https://tv.apple.com/"), new ProviderWatchOption("GB", "Apple TV", WatchProviderType.Stream, "https://tv.apple.com/") };
        var show = await ImportedAsync(Show("Pachinko") with { WatchOptions = guess, ImdbId = "tt8888462" });
        var stored = await StoredAsync(show);
        Assert.Equal("tt8888462", stored.ImdbId);

        await JustWatchAsync(stored.Id, "US", [new("Apple TV", WatchProviderType.Stream, "https://click.justwatch.com/a?r=x"), new("Prime Video", WatchProviderType.Buy, "https://click.justwatch.com/a?r=y")],
            "/us/tv-show/pachinko");
        // The provider has no IMDb id this time: the one the catalog holds stays.
        await SyncAsync(s => s.RefreshAsync(show with { ImdbId = null }, Ct));

        var rows = await WatchRowsAsync(stored.Id);
        Assert.Equal([("GB", "Apple TV", WatchSource.Catalog), ("US", "Apple TV", WatchSource.JustWatch), ("US", "Prime Video", WatchSource.JustWatch)], rows);
        Assert.Equal("tt8888462", (await StoredAsync(show)).ImdbId);
    }

    [Fact]
    public async Task A_show_JustWatch_lists_nowhere_in_a_country_has_no_rows_there()
    {
        var show = await ImportedAsync(Show("Tehran") with { WatchOptions = [new ProviderWatchOption("US", "Apple TV", WatchProviderType.Stream, null)] });
        var id = (await StoredAsync(show)).Id;

        await JustWatchAsync(id, "US", [], "/us/tv-show/tehran");
        await SyncAsync(s => s.RefreshAsync(show, Ct));

        Assert.Empty(await WatchRowsAsync(id));
        await using var db = database.Context();
        Assert.True((await db.JustWatchListings.SingleAsync(l => l.ShowId == id, Ct)).Found);
    }

    [Fact]
    public async Task A_show_JustWatch_no_longer_knows_gets_the_providers_guess_back_on_its_next_refresh()
    {
        var show = await ImportedAsync(Show("Trying") with { WatchOptions = [new ProviderWatchOption("US", "Apple TV", WatchProviderType.Stream, null)] });
        var id = (await StoredAsync(show)).Id;
        await JustWatchAsync(id, "US", [new("Apple TV", WatchProviderType.Stream, "https://click.justwatch.com/a?r=x")], "/us/tv-show/trying");

        await JustWatchAsync(id, "US", null, null);
        Assert.Empty(await WatchRowsAsync(id));
        await SyncAsync(s => s.RefreshAsync(show, Ct));

        Assert.Equal([("US", "Apple TV", WatchSource.Catalog)], await WatchRowsAsync(id));
    }

    private async Task JustWatchAsync(Guid showId, string region, IReadOnlyList<JustWatchOption>? offers, string? path)
    {
        await using var db = database.Context();
        await JustWatchService.ApplyAsync(db, showId, region, offers, path, Now.UtcDateTime, Ct);
    }

    private async Task<List<(string Region, string Provider, WatchSource Source)>> WatchRowsAsync(Guid showId)
    {
        await using var db = database.Context();
        return (await db.WatchProviders.AsNoTracking().Where(w => w.ShowId == showId).OrderBy(w => w.Region).ThenBy(w => w.ProviderName).ToListAsync(Ct))
            .Select(w => (w.Region, w.ProviderName, w.Source)).ToList();
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One call on the synchronizer, with a context and an outbox of its own as each job run has.
    /// </summary>
    private async Task<T> SyncAsync<T>(Func<CatalogSynchronizer, Task<T>> call)
    {
        await using var db = database.Context();
        var outbox = new IntegrationEventOutbox<CatalogContext>(db, bus, NullLogger<IntegrationEventOutbox<CatalogContext>>.Instance);
        return await call(new CatalogSynchronizer(db, provider, outbox, Cache, time, NullLogger<CatalogSynchronizer>.Instance));
    }

    private Task SyncAsync(Func<CatalogSynchronizer, Task> call) => SyncAsync(async s => { await call(s); return true; });

    /// <summary>
    /// Has the provider offer the show, under an id of this test's own.
    /// </summary>
    private ProviderShow Offer(ProviderShow show)
    {
        provider.Shows[show.ExternalId] = show;
        return show;
    }

    /// <summary>
    /// A show already in the catalog, with the import's own events out of the way.
    /// </summary>
    private async Task<ProviderShow> ImportedAsync(ProviderShow show)
    {
        Offer(show);
        Assert.Equal(ImportOutcome.Imported, (await SyncAsync(s => s.ImportShowAsync(show.ExternalId, Ct))).Outcome);
        bus.Clear();
        return show;
    }

    private async Task<Show> StoredAsync(ProviderShow show)
    {
        await using var db = database.Context();
        return await db.Shows.Include(s => s.Seasons).Include(s => s.Episodes).Include(s => s.Cast).ThenInclude(c => c.Person)
            .AsSplitQuery().AsNoTracking().SingleAsync(s => s.Source == provider.Name && s.ExternalId == show.ExternalId, Ct);
    }

    /// <summary>
    /// A running show as a provider gives it: one season, and an episode still to come unless told otherwise.
    /// </summary>
    private ProviderShow Show(string title, IReadOnlyList<ProviderEpisode>? episodes = null)
    {
        var id = $"{title.ToLowerInvariant().Replace(' ', '-')}-{tag}";
        episodes = [.. (episodes ?? [Episode(1, 1, "Pilot", Now.AddDays(14))]).Select(e => e with { ExternalId = $"{id}-{e.ExternalId}" })];
        return new ProviderShow(id, title, $"About {title}.", null, null, new DateOnly(2022, 2, 18), ShowStatus.Airing, "Apple TV+", ["Drama"], [], 50,
            [new ProviderSeason(1, "Season 1", null, null)], episodes, [], [], [], 8.1);
    }

    private static ProviderEpisode Episode(int season, int number, string title, DateTimeOffset? airs) =>
        new($"s{season}e{number}", season, number, title, null, airs, 50, null);

    private string Name(string person) => $"{person} {tag}";
}

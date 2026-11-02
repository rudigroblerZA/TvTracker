using Catalog.Data;
using Catalog.SyncWorker;
using Catalog.SyncWorker.Providers;
using TvTracker.IntegrationEvents;

namespace Catalog.UnitTests;

/// <summary>
/// Accepted corrections and saved research, applied over the provider's show (see CONTEXT.md, corrections).
/// </summary>
public class CatalogOverridesTests
{
    [Theory]
    [InlineData(CatalogOverrideFields.ShowSummary, "A better summary.")]
    [InlineData(CatalogOverrideFields.ShowNetwork, "HBO")]
    [InlineData(CatalogOverrideFields.ShowPremiered, "2008-01-20")]
    [InlineData(CatalogOverrideFields.EpisodeTitle, "Pilot")]
    [InlineData(CatalogOverrideFields.EpisodeAirDate, "2008-01-20")]
    [InlineData(CatalogOverrideFields.EpisodeRuntime, "58")]
    public void Valid_values(string field, string value) => Assert.Null(CatalogOverrides.Validate(field, value));

    [Theory]
    [InlineData("show-rating", "9.5")]
    [InlineData(CatalogOverrideFields.ShowPremiered, "20 January 2008")]
    [InlineData(CatalogOverrideFields.EpisodeAirDate, "2008-02-30")]
    [InlineData(CatalogOverrideFields.EpisodeRuntime, "0")]
    [InlineData(CatalogOverrideFields.EpisodeRuntime, "601")]
    [InlineData(CatalogOverrideFields.EpisodeRuntime, "an hour")]
    [InlineData(CatalogOverrideFields.ShowNetwork, "   ")]
    [InlineData(CatalogOverrideFields.EpisodeTitle, "")]
    public void Invalid_values(string field, string value) => Assert.NotNull(CatalogOverrides.Validate(field, value));

    [Fact]
    public void Overrides_patch_the_providers_show_and_episodes()
    {
        var episodeId = Guid.NewGuid();
        var show = Show(Episode("e1", 1, 1, "Pilot", new DateTimeOffset(2008, 1, 21, 2, 0, 0, TimeSpan.Zero)), Episode("e2", 1, 2, "Cat's in the Bag", null));
        var overrides = new List<CatalogOverride>
        {
            Override(null, CatalogOverrideFields.ShowNetwork, "AMC", 1),
            Override(null, CatalogOverrideFields.ShowPremiered, "2008-01-20", 1),
            Override(episodeId, CatalogOverrideFields.EpisodeTitle, "Wrong", 1),
            // A later override of the same field wins.
            Override(episodeId, CatalogOverrideFields.EpisodeTitle, "Pilot (Breaking Bad)", 2),
            Override(episodeId, CatalogOverrideFields.EpisodeAirDate, "2008-01-20", 3),
            // An episode the show doesn't list is ignored.
            Override(Guid.NewGuid(), CatalogOverrideFields.EpisodeTitle, "Nobody's", 1)
        };

        var patched = CatalogOverrides.Apply(show, overrides, new Dictionary<Guid, string> { [episodeId] = "e1" });

        Assert.Equal("AMC", patched.Network);
        Assert.Equal(new DateOnly(2008, 1, 20), patched.FirstAirDate);
        var pilot = patched.Episodes[0];
        Assert.Equal("Pilot (Breaking Bad)", pilot.Title);
        // The date changes; the provider's time of day stays.
        Assert.Equal(new DateTimeOffset(2008, 1, 20, 2, 0, 0, TimeSpan.Zero), pilot.AirsAtUtc);
        Assert.Equal(show.Episodes[1], patched.Episodes[1]);
    }

    [Fact]
    public void No_overrides_leaves_the_show_as_it_is()
    {
        var show = Show(Episode("e1", 1, 1, "Pilot", null));
        Assert.Same(show, CatalogOverrides.Apply(show, [], new Dictionary<Guid, string>()));
    }

    private static CatalogOverride Override(Guid? episodeId, string field, string value, int minute) => new()
    {
        Id = Guid.NewGuid(), ShowId = Guid.NewGuid(), EpisodeId = episodeId, Field = field, Value = value,
        CreatedAtUtc = new DateTime(2026, 10, 1, 12, minute, 0, DateTimeKind.Utc)
    };

    internal static ProviderEpisode Episode(string id, int season, int number, string title, DateTimeOffset? airs) =>
        new(id, season, number, title, null, airs, 47, null);

    internal static ProviderShow Show(params ProviderEpisode[] episodes) => Show(ShowStatus.Airing, [], episodes);

    internal static ProviderShow Show(ShowStatus status, IReadOnlyList<ProviderSeason> seasons, params ProviderEpisode[] episodes) =>
        new("169", "Breaking Bad", "A teacher turns to crime.", null, null, new DateOnly(2008, 1, 21), status, "AMC", ["Drama"], ["scripted"], 100,
            seasons, episodes, [], [], [], 9.2);
}

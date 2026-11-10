using Research.API.Data;
using Research.API.Services;

namespace Research.UnitTests;

/// <summary>
/// When saved research is due a refresh, and when the research assistant may prepare a show.
/// </summary>
public class RefreshTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_new_season_since_the_save_is_due()
    {
        Assert.Equal([RefreshReasons.NewSeason], RefreshReasons.For(Now.AddDays(-30), seasonsAtSave: 2, seasonsNow: 3, false, Now));
        Assert.Empty(RefreshReasons.For(Now.AddDays(-30), seasonsAtSave: 3, seasonsNow: 3, false, Now));
    }

    [Fact]
    public void Wikidata_disagreeing_and_age_count_too()
    {
        Assert.Equal([RefreshReasons.Wikidata], RefreshReasons.For(Now.AddDays(-30), 3, 3, wikidataDisagrees: true, Now));
        Assert.Equal([RefreshReasons.Old], RefreshReasons.For(Now.AddDays(-400), 3, 3, false, Now));
        Assert.Empty(RefreshReasons.For(Now.AddDays(-364), 3, 3, false, Now));
    }

    [Fact]
    public void Research_saved_before_seasons_were_kept_goes_by_age_and_Wikidata()
    {
        Assert.Empty(RefreshReasons.For(Now.AddDays(-30), seasonsAtSave: null, seasonsNow: 9, false, Now));
        Assert.Equal([RefreshReasons.Wikidata, RefreshReasons.Old], RefreshReasons.For(Now.AddDays(-400), null, 9, true, Now));
    }

    [Fact]
    public void Only_seasons_with_episodes_count()
    {
        var show = new CatalogShow(Guid.NewGuid(), "Silo", null, null, null,
            [new CatalogSeason(0, 2), new CatalogSeason(1, 10), new CatalogSeason(2, 10), new CatalogSeason(3, 0)], null, null, null);
        Assert.Equal(2, RefreshReasons.Seasons(show));
    }

    [Fact]
    public void The_assistant_leaves_a_workspace_changed_since_the_save_alone()
    {
        var saved = Now.AddDays(-30);
        Assert.True(ResearchAssistant.Untouched(null, saved));
        Assert.True(ResearchAssistant.Untouched(new Dossier { ShowTitle = "Silo", UpdatedAtUtc = saved }, saved));
        Assert.False(ResearchAssistant.Untouched(new Dossier { ShowTitle = "Silo", UpdatedAtUtc = saved.AddHours(1) }, saved));
    }

    [Fact]
    public void Wikidata_counts_only_after_the_save_and_for_fields_the_research_left_open()
    {
        var saved = Now.AddDays(-10);
        HashSet<string> none = [], network = ["network"];

        // Yesterday's check, from before the save, says nothing about the research saved since.
        Assert.False(RefreshReasons.WikidataDisagrees([("network", saved.AddHours(-1))], saved, none));
        Assert.True(RefreshReasons.WikidataDisagrees([("network", saved.AddDays(1))], saved, none));
        // A moderator who saved a network has judged Wikidata's, even keeping the catalog's.
        Assert.False(RefreshReasons.WikidataDisagrees([("network", saved.AddDays(1))], saved, network));
        Assert.True(RefreshReasons.WikidataDisagrees([("network", saved.AddDays(1)), ("premiered", saved.AddDays(1))], saved, network));
    }

    [Fact]
    public void Prepared_means_the_assistants_preparation_is_the_last_change()
    {
        var at = Now.AddHours(-2);
        Assert.True(ResearchAssistant.PreparedLast(new Dossier { ShowTitle = "Silo", AssistantPreparedAtUtc = at, UpdatedAtUtc = at }));
        // A moderator gathered or edited since: the work is theirs now.
        Assert.False(ResearchAssistant.PreparedLast(new Dossier { ShowTitle = "Silo", AssistantPreparedAtUtc = at, UpdatedAtUtc = at.AddMinutes(5) }));
        Assert.False(ResearchAssistant.PreparedLast(new Dossier { ShowTitle = "Silo", UpdatedAtUtc = at }));
    }

    [Fact]
    public void A_workspace_no_one_has_changed_reads_as_untouched_with_nothing_saved()
    {
        Assert.True(ResearchAssistant.Untouched(new Dossier { ShowTitle = "Silo", UpdatedAtUtc = DateTime.UnixEpoch }, DateTime.UnixEpoch));
        Assert.False(ResearchAssistant.Untouched(new Dossier { ShowTitle = "Silo", UpdatedAtUtc = Now }, DateTime.UnixEpoch));
    }
}

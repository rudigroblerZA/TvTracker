using System.Text.Json;
using Catalog.SyncWorker.Providers;
using Catalog.SyncWorker.Providers.Tvdb;
using TvTracker.IntegrationEvents;

namespace Catalog.UnitTests;

/// <summary>
/// TheTVDB's records read as the catalog's fields, from JSON shaped as its v4 API documents it.
/// </summary>
public class TvdbMappingTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private const string Series = """
        {
          "id": 81189, "name": "Breaking Bad", "overview": " A chemistry teacher turns to crime. ",
          "image": "https://artworks.thetvdb.com/banners/posters/81189-10.jpg",
          "firstAired": "2008-01-20", "lastAired": "2013-09-29", "score": 250000,
          "status": { "id": 2, "name": "Ended" },
          "originalCountry": "usa", "originalLanguage": "eng",
          "originalNetwork": { "id": 54, "name": "AMC", "country": "usa" },
          "latestNetwork": { "id": 54, "name": "AMC", "country": "usa" },
          "genres": [ { "id": 12, "name": "Drama" }, { "id": 5, "name": "Crime" }, { "id": 2, "name": "Science Fiction" } ],
          "artworks": [
            { "id": 1, "image": "/banners/fanart/original/81189-1.jpg", "type": 3, "score": 100, "language": null },
            { "id": 2, "image": "https://artworks.thetvdb.com/banners/fanart/original/81189-2.jpg", "type": 3, "score": 900, "language": "eng" },
            { "id": 3, "image": "https://artworks.thetvdb.com/banners/fanart/original/81189-3.jpg", "type": 3, "score": 5000, "language": "deu" },
            { "id": 4, "image": "https://artworks.thetvdb.com/banners/graphical/81189-g.jpg", "type": 1, "score": 10, "language": "eng" }
          ],
          "seasons": [
            { "id": 1, "number": 0, "name": "Specials", "type": { "id": 1, "name": "Aired Order", "type": "official" } },
            { "id": 2, "number": 1, "name": "", "type": { "id": 1, "name": "Aired Order", "type": "official" } },
            { "id": 3, "number": 2, "name": "The Second", "type": { "id": 1, "name": "Aired Order", "type": "official" } },
            { "id": 4, "number": 2, "name": "DVD two", "type": { "id": 2, "name": "DVD Order", "type": "dvd" } }
          ],
          "characters": [
            { "id": 10, "name": "Jesse Pinkman", "peopleId": 2, "personName": "Aaron Paul", "peopleType": "Actor", "sort": 2, "isFeatured": false,
              "personImgURL": "/banners/person/2/a.jpg" },
            { "id": 11, "name": "Walter White", "peopleId": 1, "personName": "Bryan Cranston", "peopleType": "Actor", "sort": 1, "isFeatured": true,
              "personImgURL": "https://artworks.thetvdb.com/banners/person/1/b.jpg" },
            { "id": 12, "name": "Heisenberg", "peopleId": 1, "personName": "Bryan Cranston", "peopleType": "Actor", "sort": 5, "isFeatured": false },
            { "id": 13, "name": "Wendy", "peopleId": 3, "personName": "Julia Minesci", "peopleType": "Guest Star", "sort": 0 },
            { "id": 14, "name": null, "peopleId": 4, "personName": "Vince Gilligan", "peopleType": "Creator" },
            { "id": 15, "name": null, "peopleId": 4, "personName": "Vince Gilligan", "peopleType": "Executive Producer" },
            { "id": 16, "name": null, "peopleId": 5, "personName": "Michelle MacLaren", "peopleType": "Director" }
          ]
        }
        """;

    private const string Episodes = """
        [
          { "id": 349232, "name": "Pilot", "overview": "Walter learns he has cancer.", "aired": "2008-01-20", "runtime": 58, "number": 1, "seasonNumber": 1 },
          { "id": 349235, "name": "", "aired": "2008-01-27", "runtime": 0, "number": 2, "seasonNumber": 1 },
          { "id": 999001, "name": "Pilot (duplicate)", "aired": null, "number": 1, "seasonNumber": 1 },
          { "id": 349300, "name": "Seven Thirty-Seven", "aired": "2009-03-08", "runtime": 47, "number": 1, "seasonNumber": 2 },
          { "id": 700000, "name": "Inside Breaking Bad", "aired": "2009-02-17", "number": 1, "seasonNumber": 0 },
          { "id": 700001, "name": "Unnumbered", "aired": "2009-02-18", "seasonNumber": 2 }
        ]
        """;

    private static ProviderShow Show() => TvdbMapping.Show(
        JsonSerializer.Deserialize<TvdbSeries>(Series, TvdbClient.Json)!,
        JsonSerializer.Deserialize<List<TvdbEpisode>>(Episodes, TvdbClient.Json)!,
        Today);

    [Fact]
    public void A_series_reads_as_a_show()
    {
        var show = Show();

        Assert.Equal("81189", show.ExternalId);
        Assert.Equal("Breaking Bad", show.Title);
        Assert.Equal("A chemistry teacher turns to crime.", show.Overview);
        Assert.Equal("https://artworks.thetvdb.com/banners/posters/81189-10.jpg", show.PosterUrl);
        Assert.Equal(new DateOnly(2008, 1, 20), show.FirstAirDate);
        Assert.Equal(ShowStatus.Ended, show.Status);
        Assert.Equal("AMC", show.Network);
        Assert.Equal(["Drama", "Crime", "Science-Fiction"], show.Genres);
        Assert.Equal(["scripted", "english"], show.Keywords);
        Assert.Null(show.Rating);
        Assert.Empty(show.WatchOptions);
    }

    [Fact]
    public void The_backdrop_is_the_best_scored_background_in_English_or_without_a_language()
    {
        var show = Show();

        // The German one scores highest but carries German text.
        Assert.Equal("https://artworks.thetvdb.com/banners/fanart/original/81189-2.jpg", show.BackdropUrl);
        Assert.Equal("https://artworks.thetvdb.com/banners/graphical/81189-g.jpg", show.BannerUrl);
    }

    [Fact]
    public void Episodes_are_the_numbered_regular_ones_once_each()
    {
        var episodes = Show().Episodes;

        Assert.Equal([(1, 1), (1, 2), (2, 1)], episodes.Select(e => (e.SeasonNumber, e.EpisodeNumber)));
        var pilot = episodes[0];
        // The dated record wins over its undated duplicate.
        Assert.Equal("349232", pilot.ExternalId);
        Assert.Equal("Pilot", pilot.Title);
        Assert.Equal(new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero), pilot.AirsAtUtc);
        Assert.Equal(58, pilot.RuntimeMinutes);
        Assert.Equal("Episode 2", episodes[1].Title);
        Assert.Null(episodes[1].RuntimeMinutes);
    }

    [Fact]
    public void Seasons_are_those_with_episodes_named_in_aired_order()
    {
        var seasons = Show().Seasons;

        Assert.Equal([1, 2], seasons.Select(s => s.Number));
        Assert.Equal("Season 1", seasons[0].Name);
        Assert.Equal("The Second", seasons[1].Name);
        Assert.Equal(new DateTimeOffset(2009, 3, 8, 0, 0, 0, TimeSpan.Zero), seasons[1].PremieresAtUtc);
    }

    [Fact]
    public void The_cast_is_the_regulars_featured_first_with_an_actors_characters_together()
    {
        var cast = Show().Cast;

        Assert.Equal(["Bryan Cranston", "Aaron Paul"], cast.Select(c => c.Name));
        Assert.Equal("Walter White / Heisenberg", cast[0].Character);
        Assert.Equal(["11", "12"], cast[0].CharacterExternalIds);
        Assert.Equal("1", cast[0].Person!.ExternalId);
        Assert.Equal("https://artworks.thetvdb.com/banners/person/2/a.jpg", cast[1].PhotoUrl);
    }

    [Fact]
    public void Creators_and_executive_producers_are_the_creatives()
    {
        var creative = Assert.Single(Show().Creatives!);

        Assert.Equal("Vince Gilligan", creative.Name);
        Assert.Equal(["Creator", "Executive Producer"], creative.Roles);
        Assert.Equal("4", creative.Person!.ExternalId);
    }

    [Fact]
    public void Status_comes_from_TheTVDBs_and_the_first_air_date()
    {
        Assert.Equal(ShowStatus.Ended, TvdbMapping.Status("Ended", new(2008, 1, 20), Today));
        Assert.Equal(ShowStatus.Airing, TvdbMapping.Status("Continuing", new(2022, 2, 18), Today));
        // Announced with a date still to come, or none.
        Assert.Equal(ShowStatus.Upcoming, TvdbMapping.Status("Continuing", new(2027, 1, 1), Today));
        Assert.Equal(ShowStatus.Upcoming, TvdbMapping.Status("Continuing", null, Today));
        Assert.Equal(ShowStatus.Upcoming, TvdbMapping.Status("Upcoming", null, Today));
    }

    [Fact]
    public void Popularity_grows_with_the_score_and_stays_within_100()
    {
        Assert.Equal(0, TvdbMapping.Popularity(null));
        Assert.Equal(0, TvdbMapping.Popularity(0));
        Assert.True(TvdbMapping.Popularity(100) < TvdbMapping.Popularity(10_000));
        Assert.InRange(TvdbMapping.Popularity(250_000), 80, 100);
        Assert.Equal(100, TvdbMapping.Popularity(1e12));
    }

    [Fact]
    public void A_shows_kind_is_the_format_among_its_genres()
    {
        Assert.Equal("animation", TvdbMapping.Kind([new("Comedy"), new("Animation")]));
        Assert.Equal("reality", TvdbMapping.Kind([new("Reality")]));
        Assert.Equal("scripted", TvdbMapping.Kind([new("Drama")]));
        Assert.Equal("scripted", TvdbMapping.Kind(null));
    }

    [Fact]
    public void A_search_hit_reads_as_a_summary()
    {
        var hits = JsonSerializer.Deserialize<List<TvdbSearchResult>>("""
            [
              { "objectID": "series-121361", "tvdb_id": "121361", "name": "Game of Thrones", "type": "series",
                "image_url": "https://artworks.thetvdb.com/banners/posters/121361-4.jpg", "first_air_time": "2011-04-17",
                "network": "HBO", "country": "usa", "primary_language": "eng", "status": "Ended" },
              { "objectID": "series-x", "tvdb_id": "not-a-number", "name": "Broken", "type": "series" }
            ]
            """, TvdbClient.Json)!;

        var summary = TvdbMapping.Summary(hits[0])!;
        Assert.Equal("121361", summary.ExternalId);
        Assert.Equal("Game of Thrones", summary.Title);
        Assert.Equal(new DateOnly(2011, 4, 17), summary.Premiered);
        Assert.Equal("HBO", summary.Network);
        Assert.Equal("US", summary.Country);
        Assert.Equal("english", summary.Language);
        Assert.Null(TvdbMapping.Summary(hits[1]));
    }

    [Theory]
    [InlineData("81189", true)]
    [InlineData("0", false)]
    [InlineData("081189", false)]
    [InlineData("Q1079", false)]
    [InlineData("", false)]
    [InlineData("12 ", false)]
    public void An_id_is_a_positive_whole_number(string id, bool valid) => Assert.Equal(valid, TvdbMapping.IsId(id));
}

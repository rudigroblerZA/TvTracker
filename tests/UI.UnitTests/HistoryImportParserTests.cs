using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// Bring your history's parser on the sample exports (samples/history-import), whose README says what each should give,
/// and the rules CONTEXT.md states: zone-less times in the viewer's zone, one watch per episode, account files unread.
/// </summary>
public class HistoryImportParserTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static ParsedHistory Sample(string file)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "samples", file));
        return HistoryImportParser.Parse(stream, file, NewYork);
    }

    private static ParsedHistory Text(string fileName, string content, TimeZoneInfo? zone = null) =>
        HistoryImportParser.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), fileName, zone ?? NewYork);

    [Theory]
    [InlineData("tv-time-gdpr-export.zip", 8, 83, 7)]
    [InlineData("tv-time-out-episodes.csv", 2, 18, 0)]
    [InlineData("tv-time-out-series.csv", 4, 0, 4)]
    [InlineData("trakt-export.json", 3, 22, 1)]
    [InlineData("generic-by-title.csv", 3, 19, 0)]
    [InlineData("imdb-ratings.csv", 5, 0, 0)]
    [InlineData("imdb-watchlist.csv", 3, 0, 2)]
    public void Each_sample_reads_as_its_readme_says(string file, int shows, int watches, int follows)
    {
        var history = Sample(file);

        Assert.Equal(watches, history.Watches.Count);
        Assert.Equal(follows, history.Follows.Count);
        // A file of statuses only names shows through its follows.
        Assert.Equal(shows, file == "tv-time-out-series.csv" ? history.Follows.Count : history.Shows.Count);
    }

    [Fact]
    public void IMDb_series_ratings_become_stars_and_episodes_and_movies_are_skipped()
    {
        var ratings = Sample("imdb-ratings.csv");

        // 10 and 9 are 5 stars, 8 and 7 four, 5 three; Ozymandias (an episode) and The Godfather are skipped.
        Assert.Equal([("tt0903747", 5), ("tt0108778", 4), ("tt0306414", 5), ("tt7366338", 4), ("tt11280740", 3)],
            ratings.Ratings.Select(r => (ratings.Shows[r.ShowKey].ImdbId, r.Stars)));
        Assert.Equal(2, ratings.Skipped);

        // On the watchlist, a series you've rated is a rating; the rest are planned.
        var watchlist = Sample("imdb-watchlist.csv");
        Assert.Equal(["Silo", "Shōgun"], watchlist.Follows.Select(f => watchlist.Shows[f.ShowKey].Title));
        Assert.Equal(5, Assert.Single(watchlist.Ratings).Stars);
    }

    [Fact]
    public void A_rated_series_on_the_watchlist_is_rated_not_planned()
    {
        // Both IMDb files in one archive, the watchlist read second: Severance is rated, so it's been watched.
        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
            {
                ("ratings.csv", "Const,Your Rating,Date Rated,Title,Title Type,Year\ntt11280740,9,2024-06-14,Severance,TV Series,2022\n"),
                ("watchlist.csv", "Position,Const,Created,Title,Title Type,Year,Your Rating\n1,tt11280740,2024-07-01,Severance,TV Series,2022,\n2,tt14688458,2024-07-01,Silo,TV Series,2023,\n")
            })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        zip.Position = 0;
        var history = HistoryImportParser.Parse(zip, "imdb.zip", NewYork);

        Assert.Equal(5, Assert.Single(history.Ratings).Stars);
        Assert.Equal("imdb:tt14688458", Assert.Single(history.Follows).ShowKey);
    }

    [Fact]
    public void The_tv_time_archive_never_opens_its_account_files()
    {
        var files = Sample("tv-time-gdpr-export.zip").Files;

        Assert.DoesNotContain(files, f => f.Contains("login_tokens", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("user_devices", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.Contains("tracking-prod-records-v2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_time_without_a_zone_is_read_in_the_viewers_zone_and_one_with_a_zone_as_given()
    {
        var history = Text("history.csv", "title,season,episode,watched_at\nSilo,1,1,2024-11-05 21:30:00\nSilo,1,2,2024-11-06T02:30:00Z\n");

        // Tuesday 21:30 in New York is 02:30 UTC on Wednesday: the evening stays an evening.
        Assert.Equal(new DateTimeOffset(2024, 11, 6, 2, 30, 0, TimeSpan.Zero), history.Watches.Single(w => w.Episode == 1).WatchedAtUtc);
        Assert.Equal(new DateTimeOffset(2024, 11, 6, 2, 30, 0, TimeSpan.Zero), history.Watches.Single(w => w.Episode == 2).WatchedAtUtc);
    }

    [Fact]
    public void An_episode_watched_twice_keeps_its_earliest_time()
    {
        var history = Text("history.csv", "title,season,episode,watched_at\nSilo,1,1,2025-03-01 20:00:00\nSilo,1,1,2024-11-05 21:30:00\n");

        var watch = Assert.Single(history.Watches);
        Assert.Equal(2024, watch.WatchedAtUtc!.Value.Year);
    }

    [Fact]
    public void A_row_without_an_episode_number_is_skipped_not_guessed()
    {
        var history = Text("history.csv", "title,season,episode,watched_at\nSilo,1,,2024-11-05 21:30:00\nSilo,1,2,2024-11-06 21:30:00\n");

        Assert.Single(history.Watches);
        Assert.Equal(1, history.Skipped);
    }
}

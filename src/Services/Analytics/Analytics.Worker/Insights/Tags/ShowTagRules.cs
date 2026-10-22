using Analytics.Data;
using TvTracker.IntegrationEvents;
using static Analytics.Worker.Insights.InsightMath;

namespace Analytics.Worker.Insights;

/// <summary>
/// The release pattern of the latest fully aired season: all on one day ("Drops all at once"), or in up to
/// <see cref="ShowTags.MaxDropDays"/> batches ("Drops in parts").
/// </summary>
public sealed class DropsAtOnceRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var latestFull = show.SeasonStats.LastOrDefault(s => s.AiredCount > 0 && s.AiredCount == s.EpisodeCount && s.AiredCount >= 3);
        if (latestFull is null)
        {
            return null;
        }

        var days = show.Aired.Where(e => e.SeasonNumber == latestFull.Number && e.AirsAtUtc is not null)
            .Select(e => e.AirsAtUtc!.Value.UtcDateTime.Date).Distinct().Order().ToList();
        if (days.Count == 1)
        {
            return new ShowTag(ShowTags.DropsAtOnce, "Drops all at once",
                $"Season {latestFull.Number}: all {latestFull.AiredCount} episodes on {days[0]:d MMM yyyy}");
        }

        return days.Count <= ShowTags.MaxDropDays && latestFull.AiredCount >= 2 * days.Count
            ? new ShowTag(ShowTags.DropsAtOnce, "Drops in parts", $"Season {latestFull.Number}: {latestFull.AiredCount} episodes in {days.Count} drops")
            : null;
    }
}

/// <summary>Short seasons, not too long in all, with nothing left to air (see <see cref="ShowTags.Bingeable"/>).</summary>
public sealed class BingeableRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var aired = show.Aired;
        var airedSeasons = show.SeasonStats.Count(s => s.AiredCount > 0);
        var runtime = aired.Sum(e => e.RuntimeMinutes ?? 0);
        var perSeason = airedSeasons == 0 ? 0 : (double)aired.Count / airedSeasons;
        if (!AllOut(show) || perSeason > ShowTags.BingeableMaxEpisodesPerSeason || runtime > ShowTags.BingeableMaxMinutes)
        {
            return null;
        }

        var time = runtime > 0 ? $" in {Hours(runtime)}" : "";
        return new ShowTag(ShowTags.Bingeable, "Bingeable", airedSeasons == 1
            ? $"{aired.Count} episodes{(runtime > 0 ? $" · {Hours(runtime)}" : "")}"
            : $"About {perSeason:0} episodes a season · all {aired.Count}{time}");
    }

    /// <summary>Everything announced so far has aired (no season part-way through), and there are a few episodes.</summary>
    public static bool AllOut(ShowInsightContext show) =>
        show.SeasonStats.Any(s => s.AiredCount > 0) && show.SeasonStats.All(s => s.AiredCount == s.EpisodeCount) && show.Aired.Count >= 3;
}

/// <summary>Everything aired fits in <see cref="ShowTags.WeekendBingeMaxMinutes"/>, with nothing left to air.</summary>
public sealed class WeekendBingeRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var runtime = show.Aired.Sum(e => e.RuntimeMinutes ?? 0);
        return BingeableRule.AllOut(show) && runtime is > 0 and <= ShowTags.WeekendBingeMaxMinutes
            ? new ShowTag(ShowTags.WeekendBinge, "Weekend binge", $"All of it in {Hours(runtime)}")
            : null;
    }
}

/// <summary>Our users' favourite (from the minimum number of ratings), else the provider's.</summary>
public sealed class FanFavoriteRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var rating = show.Rating;
        if (rating.Ours is >= ShowTags.FanFavoriteMinStars)
        {
            return new ShowTag(ShowTags.FanFavorite, "Fan favorite", $"★ {rating.Ours:0.0} from {rating.OurCount} TV Tracker users");
        }

        return rating.External is >= ShowTags.FanFavoriteMinExternal
            ? new ShowTag(ShowTags.FanFavorite, "Fan favorite", $"Rated {rating.External:0.0} by TVmaze's viewers")
            : null;
    }
}

/// <summary>One season, and the show has ended.</summary>
public sealed class LimitedSeriesRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show) =>
        show.SeasonStats.Count(s => s.AiredCount > 0) == 1 && show.Show.Status is ShowStatus.Ended or ShowStatus.Cancelled
            ? new ShowTag(ShowTags.LimitedSeries, "Limited series", $"One season, {show.Aired.Count} episodes")
            : null;
}

/// <summary>The typical (median) episode is at most <see cref="ShowTags.QuickEpisodeMaxMinutes"/> minutes.</summary>
public sealed class QuickEpisodesRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var runtimes = show.Aired.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
        return runtimes.Count > 0 && runtimes[runtimes.Count / 2] <= ShowTags.QuickEpisodeMaxMinutes
            ? new ShowTag(ShowTags.QuickEpisodes, "Quick episodes", $"About {runtimes[runtimes.Count / 2]} min each")
            : null;
    }
}

/// <summary>At least <see cref="ShowTags.LongRunningMinSeasons"/> seasons.</summary>
public sealed class LongRunningRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var seasons = show.SeasonStats.Count(s => s.AiredCount > 0);
        if (seasons < ShowTags.LongRunningMinSeasons)
        {
            return null;
        }

        var since = show.Aired.Min(e => e.AirsAtUtc)?.Year;
        return new ShowTag(ShowTags.LongRunning, "Long-running", since is null ? $"{seasons} seasons" : $"{seasons} seasons since {since}");
    }
}

/// <summary>The show's most prolific maker: "Prolific creator · Chuck Lorre, 19 shows behind the camera".</summary>
public sealed class ProlificMakerRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        if (!show.Sources.ProlificMakerByShow.TryGetValue(show.ShowId, out var maker))
        {
            return null;
        }

        var created = maker.Creative.Roles.Contains("Creator") || maker.Creative.Roles.Contains("Developer");
        return new ShowTag(ShowTags.ProlificCreator, created ? "Prolific creator" : "Prolific producer",
            $"{maker.Creative.Name}, {maker.Career.Shows} shows behind the camera");
    }
}

using Analytics.Data;
using TvTracker;
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
        var latestFull = show.RegularSeasonStats.LastOrDefault(IsFull);
        if (latestFull is null)
        {
            return null;
        }

        var days = show.ReleaseDays(latestFull.Number);
        if (days.Count == 1)
        {
            return new ShowTag(ShowTags.DropsAtOnce, "Drops all at once",
                $"Season {latestFull.Number}: all {latestFull.AiredCount} episodes on {days[0]:d MMM yyyy}");
        }

        return IsDrop(latestFull, days)
            ? new ShowTag(ShowTags.DropsAtOnce, "Drops in parts", $"Season {latestFull.Number}: {latestFull.AiredCount} episodes in {days.Count} drops")
            : null;
    }

    /// <summary>A season the rule can judge: fully aired, three or more episodes.</summary>
    public static bool IsFull(SeasonStats season) => season.AiredCount > 0 && season.AiredCount == season.EpisodeCount && season.AiredCount >= 3;

    /// <summary>
    /// Whether a full season came out at once or in a few batches: one release day, or at most
    /// <see cref="ShowTags.MaxDropDays"/> days with two or more episodes each on average. Shared with the DNA's
    /// release trait, so both read a release the same way.
    /// </summary>
    public static bool IsDrop(SeasonStats season, IReadOnlyList<DateTime> releaseDays) =>
        releaseDays.Count == 1 || releaseDays.Count <= ShowTags.MaxDropDays && season.AiredCount >= 2 * releaseDays.Count;
}

/// <summary>Short seasons, not too long in all, with nothing left to air (see <see cref="ShowTags.Bingeable"/>).</summary>
public sealed class BingeableRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var aired = show.RegularAired;
        var airedSeasons = show.RegularSeasonStats.Count(s => s.AiredCount > 0);
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
        show.RegularSeasonStats.Any(s => s.AiredCount > 0) && show.RegularSeasonStats.All(s => s.AiredCount == s.EpisodeCount)
        && show.RegularAired.Count >= 3;
}

/// <summary>Everything aired fits in <see cref="ShowTags.WeekendBingeMaxMinutes"/>, with nothing left to air.</summary>
public sealed class WeekendBingeRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var runtime = show.RegularAired.Sum(e => e.RuntimeMinutes ?? 0);
        return BingeableRule.AllOut(show) && runtime is > 0 and <= ShowTags.WeekendBingeMaxMinutes
            ? new ShowTag(ShowTags.WeekendBinge, "Weekend binge", $"All of it in {Hours(runtime)}")
            : null;
    }
}

/// <summary>Our users' favourite (from the minimum number of ratings), else the provider's.</summary>
public sealed class FanFavoriteRule(Branding brand) : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var rating = show.Rating;
        if (rating.Ours is >= ShowTags.FanFavoriteMinStars)
        {
            return new ShowTag(ShowTags.FanFavorite, "Fan favorite", $"★ {rating.Ours:0.0} from {rating.OurCount} {brand.AppName} users");
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
        show.RegularSeasonStats.Count(s => s.AiredCount > 0) == 1 && show.Show.HasEnded()
            ? new ShowTag(ShowTags.LimitedSeries, "Limited series", $"One season, {show.RegularAired.Count} episodes")
            : null;
}

/// <summary>The typical (median) episode is at most <see cref="ShowTags.QuickEpisodeMaxMinutes"/> minutes.</summary>
public sealed class QuickEpisodesRule : IShowTagRule
{
    public ShowTag? TagFor(ShowInsightContext show)
    {
        var runtimes = show.RegularAired.Where(e => e.RuntimeMinutes > 0).Select(e => e.RuntimeMinutes!.Value).Order().ToList();
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
        var seasons = show.RegularSeasonStats.Count(s => s.AiredCount > 0);
        if (seasons < ShowTags.LongRunningMinSeasons)
        {
            return null;
        }

        var since = show.RegularAired.Min(e => e.AirsAtUtc)?.Year;
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

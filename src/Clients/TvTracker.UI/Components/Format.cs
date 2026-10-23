using TvTracker.UI.Models;

namespace TvTracker.UI.Components;

public static class Format
{
    public static string EpisodeCode(int season, int episode) => $"S{season:00}E{episode:00}";

    public static string Status(ShowStatus status) => status switch
    {
        ShowStatus.Upcoming => "Upcoming",
        ShowStatus.Airing => "Airing",
        ShowStatus.Renewed => "Renewed",
        ShowStatus.Ended => "Ended",
        ShowStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    public static string WatchStatusLabel(WatchStatus status) => status switch
    {
        WatchStatus.Watching => "Watching",
        WatchStatus.PlanToWatch => "Plan to watch",
        WatchStatus.Completed => "Completed",
        WatchStatus.Paused => "Paused",
        WatchStatus.Stopped => "Stopped",
        _ => status.ToString()
    };

    public static string? Label(EpisodeLabel label) => label switch
    {
        EpisodeLabel.SeriesPremiere => "Series premiere",
        EpisodeLabel.SeasonPremiere => "Season premiere",
        EpisodeLabel.SeasonFinale => "Season finale",
        _ => null
    };

    public static string ProviderType(WatchProviderType type) => type switch
    {
        WatchProviderType.Stream => "Stream",
        WatchProviderType.Free => "Free",
        WatchProviderType.Rent => "Rent",
        WatchProviderType.Buy => "Buy",
        _ => type.ToString()
    };

    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min"
        : minutes < 60 * 24 ? (minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min")
        : $"{minutes / (60 * 24)} day{(minutes / (60 * 24) == 1 ? "" : "s")} {minutes % (60 * 24) / 60} h";

    /// <summary>Watching time in whole hours ("365 h") for long stretches, where days would be misleading.</summary>
    public static string Hours(int minutes) => minutes < 120 ? Duration(minutes) : $"{Math.Round(minutes / 60.0):N0} h";

    /// <summary>
    /// Whole years elapsed from <paramref name="then"/> to <paramref name="today"/> ("18 years ago", "a year ago"), not the
    /// difference of calendar years; null under a year.
    /// </summary>
    public static string? YearsAgo(DateOnly then, DateOnly today)
    {
        var years = today.Year - then.Year - (today < then.AddYears(today.Year - then.Year) ? 1 : 0);
        return years > 1 ? $"{years} years ago" : years == 1 ? "a year ago" : null;
    }

    /// <summary>A provider rating (0-10), e.g. "7.9".</summary>
    public static string Rating(double rating) => rating.ToString("0.0");

    /// <summary>Our users' average stars with how many rated, e.g. "★ 4.2 (12)".</summary>
    public static string Stars(double average, int count) => $"★ {average:0.0} ({count})";

    /// <summary>A 0-1 share as a whole percentage, e.g. "42%".</summary>
    public static string Percent(double share) => $"{share * 100:0}%";

    /// <summary>New follows in the last four weeks compared with the four before, e.g. "+12 in 4 weeks (up 50%)".</summary>
    public static string Growth(int recent, int previous) =>
        recent == 0 ? "no new follows in 4 weeks"
        : previous == 0 ? $"+{recent} in 4 weeks"
        : $"+{recent} in 4 weeks ({(recent >= previous ? "up" : "down")} {Math.Abs(recent - previous) * 100 / previous}%)";

    public static string Trend(string trend) => trend switch
    {
        "rising" => "Getting better season by season",
        "falling" => "Rated lower season by season",
        "steady" => "Consistently rated across seasons",
        _ => trend
    };

    /// <summary>"in 3 days", "2 hours ago", relative to now.</summary>
    public static string Relative(DateTimeOffset when)
    {
        var diff = when - DateTimeOffset.UtcNow;
        var past = diff < TimeSpan.Zero;
        var abs = diff.Duration();

        var text = abs.TotalMinutes < 60 ? $"{Math.Max(1, (int)abs.TotalMinutes)} min"
            : abs.TotalHours < 24 ? $"{(int)abs.TotalHours} h"
            : $"{(int)abs.TotalDays} day{((int)abs.TotalDays == 1 ? "" : "s")}";

        return past ? $"{text} ago" : $"in {text}";
    }
}

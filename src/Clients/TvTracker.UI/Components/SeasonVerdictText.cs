using TvTracker.IntegrationEvents;
using TvTracker.UI.Models;

namespace TvTracker.UI.Components;

/// <summary>The words for a season verdict, shared by the show page's highlight and Insights' list.</summary>
public static class SeasonVerdictText
{
    public static string Headline(SeasonVerdict v) => v.Verdict switch
    {
        SeasonVerdicts.BestYet => "Its best season yet",
        SeasonVerdicts.Above => "One of its better seasons",
        SeasonVerdicts.Below => "One of its weaker seasons",
        SeasonVerdicts.WeakestYet => "Its weakest season yet",
        SeasonVerdicts.InLine => "As good as the others",
        SeasonVerdicts.FirstSeason => "First season",
        _ => $"Rated {Format.Rating(v.Rating)}"
    };

    /// <summary>"good" for its best or better seasons, "warn" for weaker ones, else "info".</summary>
    public static string Tone(SeasonVerdict v) => v.Verdict switch
    {
        SeasonVerdicts.BestYet or SeasonVerdicts.Above => "good",
        SeasonVerdicts.Below or SeasonVerdicts.WeakestYet => "warn",
        _ => "info"
    };

    /// <summary>"Rated 8.6 · others 8.1 · finale a high point · 12 of 20 here finished it"</summary>
    public static string Details(SeasonVerdict v)
    {
        var parts = new List<string> { $"Rated {Format.Rating(v.Rating)}" };
        if (v.OthersMedian is { } others)
        {
            parts.Add($"others {Format.Rating(others)}");
        }
        switch (v.Finale)
        {
            case FinaleVerdicts.Strong:
                parts.Add("finale a high point");
                break;
            case FinaleVerdicts.Weak:
                parts.Add("finale below the season");
                break;
        }
        if (v.Started is { } started && v.Finished is { } finished)
        {
            parts.Add($"{finished} of {started} here finished it");
        }
        return string.Join(" · ", parts);
    }
}

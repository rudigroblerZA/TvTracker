namespace TvTracker.UI.Components;

/// <summary>The show tags search can filter by (Analytics' keys and their labels), in the order the filter lists them.</summary>
public static class ShowTagOptions
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("bingeable", "Bingeable"),
        ("weekend-binge", "Weekend binge"),
        ("drops-at-once", "Drops all at once"),
        ("fan-favorite", "Fan favorite"),
        ("prolific-creator", "From a prolific creator"),
        ("limited-series", "Limited series"),
        ("quick-episodes", "Quick episodes"),
        ("long-running", "Long-running")
    ];
}

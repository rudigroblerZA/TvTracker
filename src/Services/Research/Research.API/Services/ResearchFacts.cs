using System.Globalization;

namespace Research.API.Services;

/// <param name="CatalogField">The catalog override field this fact sets, or null for a fact kept only in the research.</param>
/// <param name="Kind">"text", "date", "number", "list" or "link".</param>
public sealed record FactDefinition(string Key, string Label, string? CatalogField, string Kind, int MaxLength = 500);

/// <summary>
/// The facts research can record, in the order they're shown. Three override the catalog field by field (summary,
/// network, premiere date), through the same overrides as accepted corrections, so TVmaze refreshes never undo them;
/// the rest live only in the saved research.
/// </summary>
public static class ResearchFacts
{
    public static readonly IReadOnlyList<FactDefinition> All =
    [
        new("summary", "Summary", "show-summary", "text", 4000),
        new("network", "Network", "show-network", "text", 128),
        new("premiered", "Premiered", "show-premiered", "date"),
        new("ended", "Ended", null, "date"),
        new("creators", "Created by", null, "list"),
        new("seasons", "Seasons", null, "number"),
        new("episodes", "Episodes", null, "number"),
        new("country", "Country", null, "list"),
        new("awards", "Awards", null, "list", 2000),
        new("tagline", "Tagline", null, "text", 300),
        new("tmdbRating", "TMDB rating", null, "text", 100),
        new("imdb", "IMDb", null, "link"),
        new("tmdb", "TMDB", null, "link"),
        new("wikipedia", "Wikipedia", null, "link"),
        new("wikidata", "Wikidata", null, "link")
    ];

    public static readonly IReadOnlyDictionary<string, FactDefinition> ByKey = All.ToDictionary(f => f.Key);

    /// <summary>The catalog's own value for a fact, from the show as Catalog.API serves it, or null.</summary>
    public static string? FromCatalog(string key, CatalogShow show) => key switch
    {
        "summary" => show.Overview,
        "network" => show.Network,
        "premiered" => show.FirstAirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "creators" => show.Creatives is { Count: > 0 } c
            ? string.Join(", ", c.Where(x => x.Roles.Any(r => r is "Creator" or "Developer")).Select(x => x.Name)) is { Length: > 0 } names ? names : null
            : null,
        "seasons" => show.Seasons.Count(s => s.Number > 0 && s.EpisodeCount > 0) is > 0 and var n ? n.ToString(CultureInfo.InvariantCulture) : null,
        _ => null
    };

    /// <summary>Checks a value; returns the problem, or null when it's fine.</summary>
    public static string? Validate(string key, string value)
    {
        if (!ByKey.TryGetValue(key, out var fact))
        {
            return $"Unknown fact \"{key}\".";
        }
        if (value.Length == 0 || value.Length > fact.MaxLength)
        {
            return $"{fact.Label} is 1 to {fact.MaxLength} characters.";
        }
        return fact.Kind switch
        {
            "date" when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => $"{fact.Label} is a date, yyyy-MM-dd.",
            "number" when !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 100000 => $"{fact.Label} is a whole number.",
            "link" when !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") => $"{fact.Label} is a web address.",
            _ => null
        };
    }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Mcp.Server.Services;
using ModelContextProtocol.Server;

namespace Mcp.Server.Tools;

/// <summary>
/// Analytics.API: what's computed hourly from the catalog and our users' activity. Figures about our users already
/// follow the privacy rule there (5 or more users or ratings), so nothing here is about one person.
/// </summary>
[McpServerToolType]
public sealed class InsightTools(Backends backends, ShowResolver resolver)
{
    private static readonly string[] DiscoveryLists =
        ["hidden-gems", "most-consistent", "rollercoasters", "best-finales", "bingeable", "weekend-binges", "fan-favorites", "worth-the-wait", "great-endings", "worth-your-time"];

    [McpServerTool(Name = "show_insights", ReadOnly = true, Idempotent = true)]
    [Description("Everything computed about a show: seasons and their ratings, top and weakest episodes, consistency and arc, finales, when it gets good, whether it sticks the landing, show DNA (12 traits), tags, forecasts for its next season (renewal, timing, rating), shared cast, and our users' viewing (followers, drop-off) where enough users are behind it. Use for questions like \"<show> insights\". landing.verdict says whether it sticks the landing: sticks (strong finale and final season), great-finale (strong finale after a weak season), finale-lets-down, fizzles (weak finale and season), weaker-last-season, or as-it-ran (ends as good as it ran). worthIt is whether it's worth the time: score 0-100, verdict (worth-it, mostly, mixed, hard-to-recommend) and the factors behind it.")]
    public async Task<JsonNode> ShowInsights([Description("The show's title or catalog id.")] string show, CancellationToken ct = default)
    {
        if (await resolver.ResolveAsync(show, ct) is not { } found)
        {
            return CatalogTools.NotFound(show);
        }
        if (await backends.GetAsync(Backends.Analytics, $"api/v1/shows/{found.Id}/insights", ct) is not { } insights)
        {
            return new JsonObject
            {
                ["show"] = found.Title,
                ["error"] = "No insights yet. Analytics computes them hourly, so a show just imported has none until the next run."
            };
        }
        return new JsonObject { ["show"] = found.Title, ["showId"] = found.Id.ToString(), ["insights"] = insights };
    }

    [McpServerTool(Name = "discover_list", ReadOnly = true, Idempotent = true)]
    [Description("A ranked discovery list: hidden-gems, most-consistent, rollercoasters, best-finales, bingeable, weekend-binges, fan-favorites, worth-the-wait (slow starters and where they turn), great-endings or worth-your-time (the best worth-it scores). Optionally within one genre.")]
    public async Task<JsonNode> DiscoverList(
        [Description("The list's key."), AllowedValues("hidden-gems", "most-consistent", "rollercoasters", "best-finales", "bingeable",
            "weekend-binges", "fan-favorites", "worth-the-wait", "great-endings", "worth-your-time")] string list,
        [Description("A genre such as Drama or Comedy; omit for all.")] string? genre = null,
        [Description("How many, 1 to 50.")] int limit = 20,
        CancellationToken ct = default)
    {
        var key = list.Trim().ToLowerInvariant();
        if (!DiscoveryLists.Contains(key))
        {
            return new JsonObject { ["error"] = $"Unknown list. Use one of: {string.Join(", ", DiscoveryLists)}." };
        }
        var query = $"api/v1/insights/discover/{key}?limit={Math.Clamp(limit, 1, 50)}" + (genre is { Length: > 0 } ? $"&genre={Uri.EscapeDataString(genre)}" : "");
        return await backends.GetAsync(Backends.Analytics, query, ct) ?? new JsonArray();
    }

    [McpServerTool(Name = "upcoming_premieres", ReadOnly = true, Idempotent = true)]
    [Description("Season premieres in the coming days, with the catch-up time before each (episodes and runtime) and how many of our users followed the show recently.")]
    public async Task<JsonNode> UpcomingPremieres(
        [Description("How many days ahead, 1 to 90.")] int days = 30,
        [Description("date (soonest first), rating (highest rated first) or anticipated (most recent follows first)."),
            AllowedValues("date", "rating", "anticipated")] string sort = "date",
        [Description("How many, 1 to 100.")] int limit = 25,
        CancellationToken ct = default) =>
        await backends.GetAsync(Backends.Analytics,
            $"api/v1/insights/premieres?days={Math.Clamp(days, 1, 90)}&sort={Uri.EscapeDataString(sort)}&limit={Math.Clamp(limit, 1, 100)}", ct) ?? new JsonArray();

    [McpServerTool(Name = "top_episodes", ReadOnly = true, Idempotent = true)]
    [Description("The best-rated episodes across the catalog, overall or within a genre, with TVmaze's rating and our users' where enough rated it.")]
    public async Task<JsonNode> TopEpisodes(
        [Description("A genre such as Drama; omit for all.")] string? genre = null,
        [Description("How many, 1 to 50.")] int limit = 20,
        CancellationToken ct = default) =>
        await backends.GetAsync(Backends.Analytics,
            $"api/v1/insights/top-episodes?limit={Math.Clamp(limit, 1, 50)}" + (genre is { Length: > 0 } ? $"&genre={Uri.EscapeDataString(genre)}" : ""), ct) ?? new JsonArray();

    [McpServerTool(Name = "network_reports", ReadOnly = true, Idempotent = true)]
    [Description("Report cards for networks and streaming services with five or more catalog shows: average rating, how their ended shows land (sticks the landing, fizzles), the share of seasons that come back, shows over after one season, the typical wait between seasons, and their best shows. 'baseline' is the whole catalog, for comparison.")]
    public async Task<JsonNode> NetworkReports(CancellationToken ct = default) =>
        await backends.GetAsync(Backends.Analytics, "api/v1/insights/networks", ct) ?? new JsonObject();

    [McpServerTool(Name = "person_insights", ReadOnly = true, Idempotent = true)]
    [Description("What's computed about a person: their shows' ratings, best show, years active and frequent co-stars; for creators, their fingerprint (the DNA their shows share), shows made their way, and kindred creators. Person ids come from show_details.")]
    public async Task<JsonNode> PersonInsights([Description("The person's catalog id.")] Guid personId, CancellationToken ct = default)
    {
        var insights = await backends.GetAsync(Backends.Analytics, $"api/v1/people/{personId}/insights", ct);
        var fingerprint = await backends.GetAsync(Backends.Analytics, $"api/v1/people/{personId}/fingerprint", ct);
        if (insights is null && fingerprint is null)
        {
            return new JsonObject { ["error"] = "No insights for that person yet." };
        }
        return new JsonObject { ["insights"] = insights, ["creatorFingerprint"] = fingerprint };
    }
}

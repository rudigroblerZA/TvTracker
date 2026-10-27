using System.ComponentModel;
using System.Text.Json.Nodes;
using Mcp.Server.Services;
using ModelContextProtocol.Server;

namespace Mcp.Server.Tools;

/// <summary>Catalog.API: shows, people, universes and holiday episodes. Public data, no user involved.</summary>
[McpServerToolType]
public sealed class CatalogTools(Backends backends, ShowResolver resolver)
{
    [McpServerTool(Name = "search_shows", ReadOnly = true, Idempotent = true)]
    [Description("Search the TV catalog by title or by description (\"time travel mystery in a small german town\"). Title matches come first, most popular first, then shows similar in meaning. Shows not yet in the catalog are imported from TVmaze.")]
    public async Task<JsonNode?> SearchShows(
        [Description("A title or a description.")] string query,
        [Description("How many results, 1 to 25.")] int limit = 10,
        CancellationToken ct = default) =>
        (await backends.GetAsync(Backends.Catalog, $"api/v1/shows?search={Uri.EscapeDataString(query)}&pageSize={Math.Clamp(limit, 1, 25)}", ct))?["items"];

    [McpServerTool(Name = "show_details", ReadOnly = true, Idempotent = true)]
    [Description("A show's catalog record: summary, status, network, genres, nanogenres, seasons, cast with character names and person ids, creators, the next and latest episode, and where to watch.")]
    public async Task<JsonNode> ShowDetails(
        [Description("The show's title (\"landman\") or catalog id.")] string show,
        [Description("Two-letter region for where to watch, e.g. US or GB.")] string region = "US",
        CancellationToken ct = default)
    {
        if (await resolver.ResolveAsync(show, ct) is not { } found)
        {
            return NotFound(show);
        }
        return await backends.GetAsync(Backends.Catalog, $"api/v1/shows/{found.Id}?region={Uri.EscapeDataString(region)}", ct) ?? NotFound(show);
    }

    [McpServerTool(Name = "season_episodes", ReadOnly = true, Idempotent = true)]
    [Description("A season's episodes: titles, air dates, runtimes and summaries.")]
    public async Task<JsonNode> SeasonEpisodes(
        [Description("The show's title or catalog id.")] string show,
        [Description("The season number.")] int season,
        CancellationToken ct = default)
    {
        if (await resolver.ResolveAsync(show, ct) is not { } found)
        {
            return NotFound(show);
        }
        return await backends.GetAsync(Backends.Catalog, $"api/v1/shows/{found.Id}/seasons/{season}", ct)
            ?? new JsonObject { ["error"] = $"{found.Title} has no season {season} in the catalog." };
    }

    [McpServerTool(Name = "person", ReadOnly = true, Idempotent = true)]
    [Description("A person's catalog record: their roles in catalog shows and their crew credits. Person ids come from show_details' cast and creators.")]
    public async Task<JsonNode> Person([Description("The person's catalog id.")] Guid personId, CancellationToken ct = default) =>
        await backends.GetAsync(Backends.Catalog, $"api/v1/people/{personId}", ct) ?? new JsonObject { ["error"] = "No such person in the catalog." };

    [McpServerTool(Name = "show_universe", ReadOnly = true, Idempotent = true)]
    [Description("The franchise a show belongs to (spin-offs, sequels, revivals, crossovers): its shows, how they link, and their seasons over time. Null universe when the show has none.")]
    public async Task<JsonNode> ShowUniverse([Description("The show's title or catalog id.")] string show, CancellationToken ct = default)
    {
        if (await resolver.ResolveAsync(show, ct) is not { } found)
        {
            return NotFound(show);
        }
        return await backends.GetAsync(Backends.Catalog, $"api/v1/shows/{found.Id}/universe", ct)
            ?? new JsonObject { ["show"] = found.Title, ["universe"] = null };
    }

    [McpServerTool(Name = "holiday_episodes", ReadOnly = true, Idempotent = true)]
    [Description("The best-known holiday episodes across the catalog, and shows made for the holiday. Holidays: halloween, thanksgiving, christmas, new-year, valentines.")]
    public async Task<JsonNode> HolidayEpisodes([Description("halloween, thanksgiving, christmas, new-year or valentines.")] string holiday, CancellationToken ct = default) =>
        await backends.GetAsync(Backends.Catalog, $"api/v1/holidays/{Uri.EscapeDataString(holiday.Trim().ToLowerInvariant())}", ct)
            ?? new JsonObject { ["error"] = "Unknown holiday. Use halloween, thanksgiving, christmas, new-year or valentines." };

    internal static JsonObject NotFound(string show) =>
        new() { ["error"] = $"No show matching \"{show}\" in the catalog. Try search_shows." };
}

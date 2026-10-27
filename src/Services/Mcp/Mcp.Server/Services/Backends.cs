using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Mcp.Server.Services;

/// <summary>
/// The two services the tools read, as JSON. Answers are passed through rather than remodelled, less image URLs,
/// which cost tokens and say nothing to a model. A 404 is null, so a tool can say what wasn't found.
/// </summary>
public sealed class Backends(IHttpClientFactory factory)
{
    public const string Catalog = "catalog";
    public const string Analytics = "analytics";

    private static readonly HashSet<string> ImageFields = ["posterUrl", "backdropUrl", "bannerUrl", "photoUrl", "imageUrl"];

    public async Task<JsonNode?> GetAsync(string service, string path, CancellationToken ct)
    {
        using var response = await factory.CreateClient(service).GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return Trim(await response.Content.ReadFromJsonAsync<JsonNode>(ct));
    }

    private static JsonNode? Trim(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).Where(ImageFields.Contains).ToList())
                {
                    obj.Remove(key);
                }
                foreach (var (_, value) in obj)
                {
                    Trim(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Trim(item);
                }
                break;
        }
        return node;
    }
}

/// <summary>Turns what someone typed ("landman", or a show id) into a catalog show.</summary>
public sealed class ShowResolver(Backends backends)
{
    public sealed record Show(Guid Id, string Title, int? Year);

    /// <summary>
    /// An id is taken as is; otherwise catalog search, preferring an exact title match (case-insensitive), then the
    /// first result, which is the most popular title match. Search also imports a show from TVmaze that isn't in the
    /// catalog yet, though its insights only exist after Analytics' next hourly run.
    /// </summary>
    public async Task<Show?> ResolveAsync(string show, CancellationToken ct)
    {
        if (Guid.TryParse(show, out var id))
        {
            return await backends.GetAsync(Backends.Catalog, $"api/v1/shows/{id}", ct) is { } detail
                ? new Show(id, (string)detail["title"]!, null)
                : null;
        }

        var results = await backends.GetAsync(Backends.Catalog, $"api/v1/shows?search={Uri.EscapeDataString(show.Trim())}&pageSize=10", ct);
        var items = results?["items"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var match = items.FirstOrDefault(i => string.Equals((string?)i["title"], show.Trim(), StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        return match is null ? null : new Show(Guid.Parse((string)match["id"]!), (string)match["title"]!, (int?)match["year"]);
    }
}

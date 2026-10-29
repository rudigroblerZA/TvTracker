using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.API.Services;

/// <summary>A show a tool result named, for linking it in the answer.</summary>
public record ShowRef(Guid Id, string Title);

/// <summary>A tool's answer: compact text for the model, the shows it named, and every id in it (for checking the ids
/// the model passes to later calls).</summary>
public record ToolOutput(string Text, IReadOnlyList<ShowRef> Shows, bool IsError, IReadOnlyCollection<Guid>? Ids = null);

/// <summary>
/// The app's MCP tools, through the MCP gateway (<c>Assistant:GatewayUrl</c>, <c>Assistant:GatewayKey</c>): the same
/// read-only tools Claude Code and Open WebUI use, so the assistant sees what the app shows and nothing else.
/// <list type="bullet">
/// <item><b>Compact results:</b> tool results run to 10,000 characters of JSON, much of it ids and nulls. Nulls, empty
/// values and show/episode ids go before the model sees them (shows are asked for by title), and each result is cut at
/// <see cref="MaxResultChars"/>; a small model also reads a short result better.</item>
/// <item><b>Show links:</b> shows are collected from the full result (an id with a title) before ids are removed.</item>
/// <item><b>One client,</b> kept open and listed every <see cref="ListFor"/>; dropped and reconnected after a failure.</item>
/// </list>
/// </summary>
public sealed class Toolbox(IConfiguration config, IHttpClientFactory httpFactory, ILoggerFactory loggers, ILogger<Toolbox> logger)
    : IAsyncDisposable
{
    public const int MaxResultChars = 5000;
    private static readonly TimeSpan ListFor = TimeSpan.FromMinutes(5);

    // Ids the model needs to call another tool are kept: person tools take a person id.
    private static readonly HashSet<string> DroppedIds = new(StringComparer.OrdinalIgnoreCase)
        { "id", "showId", "episodeId", "seasonId", "universeId", "sourceId" };

    private readonly SemaphoreSlim gate = new(1, 1);
    private McpClient? client;
    private IList<Tool> tools = [];
    private DateTimeOffset listedAt = DateTimeOffset.MinValue;

    public bool Configured => config["Assistant:GatewayUrl"] is { Length: > 0 };

    public async Task<IList<Tool>> ListAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - listedAt < ListFor && tools.Count > 0)
        {
            return tools;
        }
        try
        {
            var mcp = await ClientAsync(ct);
            tools = await mcp.ListToolsAsync(cancellationToken: ct) is var listed ? listed.Select(t => t.ProtocolTool).ToList() : [];
            listedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or IOException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("The MCP gateway couldn't list its tools ({Error})", ex.Message);
            await DropAsync();
        }
        return tools;
    }

    public async Task<ToolOutput> CallAsync(string name, JsonObject? arguments, CancellationToken ct)
    {
        try
        {
            var mcp = await ClientAsync(ct);
            var args = arguments?.ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone()) ?? [];
            var result = await mcp.CallToolAsync(name, args, cancellationToken: ct);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            return result.IsError == true ? new ToolOutput(Clip(text), [], true) : Compact(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or IOException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("{Tool} failed through the MCP gateway ({Error})", name, ex.Message);
            await DropAsync();
            return new ToolOutput($"The tool didn't answer: {ex.Message}", [], true);
        }
    }

    /// <summary>The result without ids, nulls and empty values, cut to size, and the shows it named.</summary>
    internal static ToolOutput Compact(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return new ToolOutput(Clip(text), [], false);
        }

        var shows = new List<ShowRef>();
        Collect(node, shows);
        var ids = new HashSet<Guid>();
        CollectIds(node, ids);
        var trimmed = Trim(node);
        return new ToolOutput(Clip(trimmed?.ToJsonString() ?? ""), shows.DistinctBy(s => s.Id).ToList(), false, ids);
    }

    private static void Collect(JsonNode? node, List<ShowRef> shows)
    {
        switch (node)
        {
            case JsonObject obj:
                var id = Text(obj, "showId") ?? (obj.ContainsKey("title") || obj.ContainsKey("overview") ? Text(obj, "id") : null);
                var title = Text(obj, "showTitle") ?? Text(obj, "title") ?? Text(obj, "show");
                if (Guid.TryParse(id, out var guid) && title is { Length: > 0 })
                {
                    shows.Add(new ShowRef(guid, title));
                }
                foreach (var (_, child) in obj)
                {
                    Collect(child, shows);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Collect(child, shows);
                }
                break;
        }
    }

    private static void CollectIds(JsonNode? node, HashSet<Guid> ids)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    CollectIds(child, ids);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    CollectIds(child, ids);
                }
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id):
                ids.Add(id);
                break;
        }
    }

    private static string? Text(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var value) && value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? Trim(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    if (DroppedIds.Contains(key) || key.EndsWith("Url", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (Trim(child) is { } kept)
                    {
                        copy[key] = kept;
                    }
                }
                return copy.Count == 0 ? null : copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var child in array)
                {
                    if (Trim(child) is { } kept)
                    {
                        items.Add(kept);
                    }
                }
                return items.Count == 0 ? null : items;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length == 0:
                return null;
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }

    private static string Clip(string text) => text.Length <= MaxResultChars ? text : text[..MaxResultChars] + " …(cut)";

    private async Task<McpClient> ClientAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (client is not null)
            {
                return client;
            }
            var key = config["Assistant:GatewayKey"];
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config["Assistant:GatewayUrl"] ?? throw new InvalidOperationException("Assistant:GatewayUrl isn't set.")),
                Name = "mcp-gateway",
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = key is { Length: > 0 } ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" } : null
            }, httpFactory.CreateClient("mcp-gateway"), loggers, ownsHttpClient: true);
            client = await McpClient.CreateAsync(transport, new McpClientOptions(), loggers, ct);
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DropAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync();
                client = null;
                listedAt = DateTimeOffset.MinValue;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }
}

using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Mcp.Gateway;

/// <param name="Url">The upstream's MCP endpoint (Streamable HTTP), e.g. <c>http://mcp-server/mcp</c>.</param>
/// <param name="BearerToken">Sent upstream as <c>Authorization: Bearer …</c>, for a server with its own key.</param>
/// <param name="Tools">Only these of its tools are exposed; empty exposes all.</param>
public sealed record UpstreamOptions(string Url, string? BearerToken = null, string[]? Tools = null);

/// <summary>
/// The MCP servers behind the gateway (<c>Gateway:Servers:{name}</c>). Their tools are listed as one set: under their
/// own names, or as <c>{server}_{tool}</c> when two servers have a tool of the same name.
/// <list type="bullet">
/// <item><b>Partial outages:</b> a server that's down is left out of the list (one warning per refresh) rather than
/// failing it, so the others' tools stay usable.</item>
/// <item><b>Cached list:</b> tools are re-listed at most every <see cref="ListFor"/>, and straight away when a call
/// names a tool the gateway doesn't know.</item>
/// <item><b>One client per server,</b> kept open; it's dropped and reconnected after a failed call, which is retried
/// once, so an upstream restart costs one slow call rather than an error.</item>
/// </list>
/// </summary>
public sealed class Upstreams(IConfiguration config, IHttpClientFactory httpFactory, ILoggerFactory loggers, ILogger<Upstreams> logger)
    : IAsyncDisposable
{
    private static readonly TimeSpan ListFor = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, UpstreamOptions> servers = config.GetSection("Gateway:Servers").GetChildren()
        .Where(s => s["Url"] is { Length: > 0 })
        .ToDictionary(s => s.Key, s => new UpstreamOptions(s["Url"]!, s["BearerToken"],
            s.GetSection("Tools").GetChildren().Select(t => t.Value!).Where(t => t is { Length: > 0 }).ToArray()));

    private readonly Dictionary<string, McpClient> clients = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, (string Server, Tool Tool)> tools = [];
    private DateTimeOffset listedAt = DateTimeOffset.MinValue;

    public IReadOnlyCollection<string> Names => servers.Keys;

    public async Task<IList<Tool>> ListAsync(CancellationToken ct)
    {
        await RefreshAsync(force: false, ct);
        return tools.Select(t => t.Key == t.Value.Tool.Name ? t.Value.Tool : Renamed(t.Value.Tool, t.Key)).ToList();
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken ct)
    {
        if (!tools.ContainsKey(request.Name))
        {
            await RefreshAsync(force: true, ct);
        }
        if (!tools.TryGetValue(request.Name, out var target))
        {
            return Error($"No tool named \"{request.Name}\" behind this gateway.");
        }

        var forward = new CallToolRequestParams { Name = target.Tool.Name, Arguments = request.Arguments, Meta = request.Meta };
        var timer = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var client = await ClientAsync(target.Server, ct);
                var result = await client.CallToolAsync(forward, ct);
                logger.LogInformation("{Tool} on {Server}: {Outcome} in {Ms} ms",
                    target.Tool.Name, target.Server, result.IsError == true ? "error" : "ok", timer.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException or IOException && attempt == 1 && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Server} failed a call to {Tool}; reconnecting and trying once more", target.Server, target.Tool.Name);
                await DropAsync(target.Server);
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException or IOException)
            {
                logger.LogWarning(ex, "{Tool} on {Server} failed after {Ms} ms", target.Tool.Name, target.Server, timer.ElapsedMilliseconds);
                await DropAsync(target.Server);
                return Error($"The {target.Server} server didn't answer: {ex.Message}");
            }
        }
    }

    private async Task RefreshAsync(bool force, CancellationToken ct)
    {
        if (!force && DateTimeOffset.UtcNow - listedAt < ListFor)
        {
            return;
        }

        var found = new List<(string Server, Tool Tool)>();
        foreach (var (name, options) in servers)
        {
            try
            {
                var client = await ClientAsync(name, ct);
                string? cursor = null;
                do
                {
                    var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct);
                    found.AddRange(page.Tools
                        .Where(t => options.Tools is not { Length: > 0 } allowed || allowed.Contains(t.Name))
                        .Select(t => (name, t)));
                    cursor = page.NextCursor;
                }
                while (cursor is not null);
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("{Server} couldn't list its tools ({Error}); leaving them out until it answers", name, ex.Message);
                await DropAsync(name);
            }
        }

        var clashing = found.GroupBy(t => t.Tool.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        tools = found.ToDictionary(t => clashing.Contains(t.Tool.Name) ? $"{t.Server}_{t.Tool.Name}" : t.Tool.Name);
        listedAt = DateTimeOffset.UtcNow;
    }

    private async Task<McpClient> ClientAsync(string server, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (clients.TryGetValue(server, out var existing))
            {
                return existing;
            }

            var options = servers[server];
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(options.Url),
                Name = server,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = options.BearerToken is { Length: > 0 } token
                    ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
                    : null
            }, httpFactory.CreateClient(server), loggers, ownsHttpClient: true);
            var client = await McpClient.CreateAsync(transport, new McpClientOptions(), loggers, ct);
            clients[server] = client;
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DropAsync(string server)
    {
        await gate.WaitAsync();
        try
        {
            if (clients.Remove(server, out var client))
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static Tool Renamed(Tool tool, string name) => new()
    {
        Name = name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        Annotations = tool.Annotations,
        Meta = tool.Meta
    };

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients.Values)
        {
            await client.DisposeAsync();
        }
    }
}

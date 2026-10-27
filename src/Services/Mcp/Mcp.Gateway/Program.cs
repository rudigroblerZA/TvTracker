using Mcp.Gateway;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<Upstreams>();

// Stateless like the servers behind it; each request lists or calls through the upstreams' kept-open clients.
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "tvtracker-gateway", Version = "1.0.0" };
        options.ServerInstructions = "One endpoint for the app's MCP servers. Tools keep their servers' names and " +
            "descriptions; see each server's own instructions in its tool descriptions.";
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithListToolsHandler(async (context, ct) =>
        new ListToolsResult { Tools = await context.Services!.GetRequiredService<Upstreams>().ListAsync(ct) })
    .WithCallToolHandler(async (context, ct) =>
        await context.Services!.GetRequiredService<Upstreams>().CallAsync(context.Params!, ct));

var app = builder.Build();

app.MapDefaultEndpoints();

// Every caller needs Gateway:ApiKey as a bearer token. Without a key, the endpoint exists only in Development, like
// the servers behind it.
var apiKey = app.Configuration["Gateway:ApiKey"];
if (apiKey is { Length: > 0 })
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Headers.Authorization != $"Bearer {apiKey}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });
}
if (apiKey is { Length: > 0 } || app.Environment.IsDevelopment())
{
    app.MapMcp("/mcp");
}

app.MapGet("/api/v1/status", (Upstreams upstreams) => new { Service = "mcp-gateway", Status = "ok", Servers = upstreams.Names });

app.Run();

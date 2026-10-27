using Mcp.Server.Services;
using Mcp.Server.Tools;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddHttpClient(nameof(ServiceToken));
builder.Services.AddSingleton<ServiceToken>();
builder.Services.AddTransient<ServiceTokenHandler>();
builder.Services.AddHttpClient(Backends.Catalog, client => client.BaseAddress = new Uri("https+http://catalog-api/"));
builder.Services.AddHttpClient(Backends.Analytics, client => client.BaseAddress = new Uri("https+http://analytics-api/"))
    .AddHttpMessageHandler<ServiceTokenHandler>();
builder.Services.AddSingleton<Backends>();
builder.Services.AddSingleton<ShowResolver>();

// Stateless: every call is a plain HTTP request, so the server can restart or scale without sessions.
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "tvtracker", Version = "1.0.0" };
        options.ServerInstructions =
            "Read-only questions about the TV catalog and what's computed from it. A show can be named by title " +
            "(\"landman\") or id; show_insights answers most \"<show> insights\" questions, show_details has the cast " +
            "and person ids for person and person_insights.";
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<CatalogTools>()
    .WithTools<InsightTools>();

var app = builder.Build();

app.MapDefaultEndpoints();

// Internal: with Mcp:ApiKey set, callers send it as a bearer token; without one, the endpoint exists only in
// Development (like the dashboard's other tools), since it reads Analytics with the server's own token.
var apiKey = app.Configuration["Mcp:ApiKey"];
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

app.MapGet("/api/v1/status", () => new { Service = "mcp-server", Status = "ok" });

app.Run();

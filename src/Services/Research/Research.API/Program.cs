using TvTracker;
using Research.API.Apis;
using Research.API.Data;
using Research.API.IntegrationEvents;
using Research.API.Services;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();
builder.AddFeatureFlags();

builder.AddNpgsqlDbContext<ResearchContext>("researchdb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
builder.Services.AddDatabaseInitializer<ResearchContext>();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>();
builder.Services.AddIntegrationEventOutbox<ResearchContext>();
builder.Services.AddHostedService<EpisodeNotesPublisher>();
// The import CLI can run the checks now (tvtracker-import wikidata run research): the job and the endpoint share this.
builder.Services.AddKeyedSingleton(CatalogChecks.JobName, new JobRun(CatalogChecks.JobName,
    $"The {CatalogChecks.Popular} most popular shows compared with Wikidata (network, premiere, episodes) and their awards read; done counts shows compared."));
builder.Services.AddHostedService<CatalogChecks>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Researchers>();
builder.Services.AddSingleton<InsightDrafter>();
builder.Services.AddSingleton<DraftQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DraftQueue>());
builder.Services.AddSingleton<ResearchAssistant>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResearchAssistant>());
builder.Services.AddSingleton<WebPageSource>();

// Shows come from Catalog.API like any client's; saved catalog fields go to the sync worker's overrides.
builder.Services.AddHttpClient<CatalogReader>(http => http.BaseAddress = new Uri("https+http://catalog-api/"));
builder.Services.AddServiceToken("Internal", IdentitySettings.InternalClient);
builder.Services.AddHttpClient<CatalogOverrides>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"))
    .AddHttpMessageHandler<ServiceTokenHandler>();

builder.Services.AddSingleton<WikimediaSource>();
builder.Services.AddSingleton<TmdbSource>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpsApi();
}

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "research-api", Status = "ok" });
app.MapResearchApi();
app.MapEpisodeNotesApi();
app.MapResearchQueueApi();
// Internal only, for the import CLI: how the Wikidata checks are doing, and running them now.
var checks = app.MapGroup("/api/v1/research/checks").RequireAuthorization(AuthenticationExtensions.InternalPolicy);
checks.MapGet("/", ([FromKeyedServices(CatalogChecks.JobName)] JobRun run) => Results.Ok(new[] { run.Status }));
checks.MapPost("/run", ([FromKeyedServices(CatalogChecks.JobName)] JobRun run) =>
    run.Request() ? Results.Accepted(value: run.Status) : Results.Conflict(run.Status));
app.MapAwardsApi();
app.MapExportApi();

app.Run();

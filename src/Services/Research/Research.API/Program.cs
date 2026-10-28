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
builder.Services.AddHostedService<CatalogChecks>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Researchers>();
builder.Services.AddSingleton<InsightDrafter>();
builder.Services.AddSingleton<DraftQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DraftQueue>());
builder.Services.AddSingleton<WebPageSource>();

// Shows come from Catalog.API like any client's; saved catalog fields go to the sync worker's overrides.
builder.Services.AddHttpClient<CatalogReader>(http => http.BaseAddress = new Uri("https+http://catalog-api/"));
builder.Services.AddHttpClient<CatalogOverrides>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"));

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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/v1/status", () => new { Service = "research-api", Status = "ok" });
app.MapResearchApi();
app.MapEpisodeNotesApi();
app.MapResearchQueueApi();

app.Run();

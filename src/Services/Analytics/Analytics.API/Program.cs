using TvTracker.UserPlans;
using Analytics.API.Apis;
using Analytics.API.Services;
using Analytics.Data;
using TvTracker;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFeatureFlags();
builder.Services.AddCopiedUserPlans<AnalyticsContext>();
builder.AddDefaultAuthentication();

// Analytics.API owns the analyticsdb schema (EF Core migrations); the worker waits for this service to be healthy.
builder.AddNpgsqlDbContext<AnalyticsContext>("analyticsdb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
builder.Services.AddDatabaseInitializer<AnalyticsContext>();

// In memory only: the time machine's weeks, the same for everyone in a time zone on a given day.
builder.Services.AddHybridCache();
builder.Services.AddSingleton(TimeProvider.System);
// Service logos (ServicesApi): one try each, without ServiceDefaults' retries, so a slow or refusing favicon service
// doesn't hold the directory up.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it undoes the ServiceDefaults handler for this client.
builder.Services.AddHttpClient("logos", client => client.Timeout = TimeSpan.FromSeconds(10)).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
// Taste Match asks Catalog.API, which has the shows' vectors, how alike two sets of shows are; signed in as a service.
builder.Services.AddServiceToken("Internal", IdentitySettings.InternalClient);
builder.Services.AddHttpClient<CatalogMatchClient>(http => http.BaseAddress = new Uri("https+http://catalog-api/"))
    .AddHttpMessageHandler<ServiceTokenHandler>();
// And Social.API, which keeps blocks, before one member answers another's match.
builder.Services.AddHttpClient<SocialBlocksClient>(http => http.BaseAddress = new Uri("https+http://social-api/"))
    .AddHttpMessageHandler<ServiceTokenHandler>();
builder.Services.AddSingleton<MatchMetrics>();
// Posters for Taste Match's share card: one quick try each, and the card is drawn without any that don't come.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient(TasteMatchApi.PosterClient, client => client.Timeout = TimeSpan.FromSeconds(5)).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // For Catalog.API's ops dashboard (/ops); not signed in, so Development only.
    app.MapOpsApi();
}

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "analytics-api", Status = "ok" });
app.MapInsightsApi();
app.MapTasteMatchLinkApi();

app.Run();

using Analytics.API.Apis;
using Analytics.Data;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFeatureFlags();
builder.Services.AddUserPlans<Analytics.API.Services.CopiedUserPlans>();
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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/v1/status", () => new { Service = "analytics-api", Status = "ok" });
app.MapInsightsApi();

app.Run();

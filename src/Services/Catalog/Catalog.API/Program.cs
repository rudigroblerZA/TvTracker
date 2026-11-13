using TvTracker;
using Catalog.API.Alerts;
using Catalog.API.Apis;
using Catalog.API.Services;
using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFeatureFlags();

// Catalog endpoints are public, but a signed-in caller's token identifies them for search rate limiting.
builder.AddDefaultAuthentication();

// Catalog.API owns the catalogdb schema; the sync worker waits for this service to be healthy.
builder.AddCatalogDbContext();
builder.Services.AddDatabaseInitializer<CatalogContext>(async (db, services, ct) =>
{
    // A migration may have just created the pgvector extension; connections opened earlier didn't know its types.
    // The initializer holds the context's connection open (its lock), so this asks the context for it rather than
    // opening it a second time.
    await db.Database.OpenConnectionAsync(ct);
    await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync(ct);
    await db.Database.CloseConnectionAsync();

    // In development with the test network running, an empty affiliate programs table starts with a test program per
    // main service. Only when empty: programs an admin removed don't come back while any other is left.
    if (services.GetRequiredService<IHostEnvironment>().IsDevelopment()
        && AffiliateLinks.TestNetwork(services.GetRequiredService<IConfiguration>()) is not null
        && !await db.AffiliatePrograms.AnyAsync(ct))
    {
        db.AffiliatePrograms.AddRange(StarterPrograms.Missing([], services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
    }
});

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.Services.AddSingleton(TimeProvider.System);

// Search asks the sync worker to import provider matches first; the worker owns provider calls and writes.
// No retries: when the worker is down, search should answer from the catalog straight away.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it undoes the ServiceDefaults handler for this client.
builder.Services.AddServiceToken("Internal", IdentitySettings.InternalClient);
builder.Services.AddHttpClient<CatalogImportClient>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"))
    .RemoveAllResilienceHandlers()
    .AddHttpMessageHandler<ServiceTokenHandler>();
// For the ops dashboard (Development only): the other services' stats, skipped quickly when a service is down.
builder.Services.AddHttpClient("analytics", http =>
    {
        http.BaseAddress = new Uri("https+http://analytics-api/");
        http.Timeout = TimeSpan.FromSeconds(3);
    })
    .RemoveAllResilienceHandlers();
builder.Services.AddHttpClient("research", http =>
    {
        http.BaseAddress = new Uri("https+http://research-api/");
        http.Timeout = TimeSpan.FromSeconds(3);
    })
    .RemoveAllResilienceHandlers();
builder.Services.AddHttpClient("catalog-sync", http =>
    {
        http.BaseAddress = new Uri("https+http://catalog-sync/");
        http.Timeout = TimeSpan.FromSeconds(3);
    })
    .RemoveAllResilienceHandlers()
    .AddHttpMessageHandler<ServiceTokenHandler>();
#pragma warning restore EXTEXP0001
builder.Services.AddSearchRateLimiting(builder.Configuration);

// Search also lists shows similar in meaning to the term, by comparing its vector with the sync worker's show vectors.
builder.Services.AddLocalEmbeddings(builder.Configuration, ShowEmbedding.Dimensions);
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddSingleton<Universes>();
builder.Services.AddSingleton<DegreesGraph>();
builder.Services.AddSingleton<Crossovers>();

// Affiliate programs: an admin's from Settings (the affiliate_programs table), then the configuration's (read on each
// click, so an edited appsettings file applies without a restart), then the development test network's catch-all, so a
// real program for a service still wins.
builder.Services.Configure<AffiliateOptions>(builder.Configuration.GetSection("Affiliates"));
builder.Services.AddScoped<AffiliatePrograms>();

// Publishes only (crossover alerts); Catalog.API subscribes to nothing.
builder.AddRabbitMQEventBus("eventbus");
builder.Services.AddHostedService<CrossoverAlerts>();
builder.Services.AddAlert<DeadLettersAlert>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseDefaultAuthentication();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Developer dashboard at /ops; not signed in, so Development only.
    app.MapOpsApi();
}

app.MapGet("/api/v1/status", () => new { Service = "catalog-api", Status = "ok" });
app.MapCatalogApi();
app.MapDeadLettersApi();

app.Run();

using Catalog.Data;
using Catalog.Embeddings;
using Catalog.SyncWorker;
using Catalog.SyncWorker.Nanogenres;
using Catalog.SyncWorker.Providers;
using Catalog.SyncWorker.Quotes;
using Catalog.SyncWorker.Settings;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The schema is created by Catalog.API; the AppHost starts this worker once that service is healthy.
builder.AddCatalogDbContext();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowFollowedIntegrationEvent, ShowFollowedHandler>()
    .AddSubscription<ShowTagsChangedIntegrationEvent, ShowTagsChangedHandler>();
builder.Services.AddIntegrationEventOutbox<CatalogContext>();

builder.Services.AddSingleton(TimeProvider.System);

// Catalog:Provider picks the data source: "tvmaze" (default) or "mock" for invented offline data.
if (string.Equals(builder.Configuration["Catalog:Provider"], "mock", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ICatalogProvider, MockCatalogProvider>();
}
else
{
    builder.Services.Configure<TvMazeOptions>(builder.Configuration.GetSection("TvMaze"));
    builder.Services.AddSingleton<TvMazeRateLimiter>();
    builder.Services.AddSingleton<TvMazeEmbedOutage>();
    builder.Services.AddTransient<TvMazeRateLimitHandler>();
    builder.Services.AddHttpClient<ICatalogProvider, TvMazeCatalogProvider>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<TvMazeOptions>>().Value.BaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TvTracker/1.0");
        })
        .AddHttpMessageHandler<TvMazeRateLimitHandler>();
}

builder.Services.AddScoped<CatalogSynchronizer>();
builder.Services.AddScoped<HistoryResolver>();
builder.Services.AddSingleton<CatalogReplayRunner>();
builder.Services.AddSingleton<BulkImportRunner>();
builder.Services.AddHostedService<CatalogSyncService>();
// Creators and executive producers: backfills older shows and keeps career figures current.
builder.Services.AddHostedService<CreativesService>();
builder.Services.AddHostedService<CastCreditsService>();
// Memorable lines from Wikiquote, for show and episode pages.
builder.Services.AddHostedService<QuotesService>();
// Where shows are set, from Wikidata, for the TV atlas.
builder.Services.AddHostedService<SettingsService>();
builder.Services.AddHostedService<Catalog.SyncWorker.Namesakes.NamesakesService>();

// Vectors of each show's text, for semantic search in Catalog.API.
// The sync worker pulls a Docker Model Runner model that isn't there yet; Catalog.API just waits for it.
builder.Services.AddLocalEmbeddings(builder.Configuration, ShowEmbedding.Dimensions, pullModel: true);
builder.Services.AddHostedService<ShowEmbeddingIndexer>();
builder.Services.AddHostedService<NanogenreService>();

var app = builder.Build();

app.MapDefaultEndpoints();

// Internal only: Catalog.API calls this before searching. Not exposed outside the Aspire network.
app.MapImportApi();
app.MapOverridesApi();

app.Run();

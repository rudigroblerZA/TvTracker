using Catalog.SyncWorker.Alerts;
using Catalog.Data;
using Catalog.Embeddings;
using Catalog.SyncWorker;
using Catalog.SyncWorker.Nanogenres;
using Catalog.SyncWorker.Providers;
using Catalog.SyncWorker.Providers.Wikidata;
using Catalog.SyncWorker.Quotes;
using Catalog.SyncWorker.Settings;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// The import, replay and override APIs answer only other services' internal client (BACKLOG S2).
builder.AddDefaultAuthentication();

// The schema is created by Catalog.API; the AppHost starts this worker once that service is healthy.
builder.AddCatalogDbContext();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowFollowedIntegrationEvent, ShowFollowedHandler>()
    .AddSubscription<ShowTagsChangedIntegrationEvent, ShowTagsChangedHandler>();
builder.Services.AddIntegrationEventOutbox<CatalogContext>();

builder.Services.AddSingleton(TimeProvider.System);

// Catalog:Provider picks the data source: "tvmaze" (default), "wikidata" (CC0) or "mock" for invented offline data.
var catalogProvider = CatalogSources.Configured(builder.Configuration);
if (catalogProvider == CatalogSources.Mock)
{
    builder.Services.AddSingleton<ICatalogProvider, MockCatalogProvider>();
}
else if (catalogProvider == CatalogSources.Wikidata)
{
    // One try per request, within the query service's own 60 s limit: Wikimedia's 429s are waited out by the client.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it undoes the ServiceDefaults handler for this client.
    builder.Services.AddHttpClient(WikidataSparql.HttpClientName, http =>
        {
            http.Timeout = TimeSpan.FromSeconds(70);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TvTracker-catalog/1.0 (+https://github.com/rudigroblerZA/TvTracker)");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    builder.Services.AddSingleton<WikidataSparql>();
    builder.Services.AddSingleton<ICatalogProvider, WikidataCatalogProvider>();
}
else
{
    builder.Services.Configure<TvMazeOptions>(builder.Configuration.GetSection("TvMaze"));
    builder.Services.AddSingleton<TvMazeRateLimiter>();
    builder.Services.AddSingleton<TvMazeUsage>();
    builder.Services.AddAlert<TvMazeErrorsAlert>();
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
builder.Services.AddSingleton<ProviderChanges>();
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
app.UseDefaultAuthentication();

// Internal only: other services call these with the internal client's token (AuthenticationExtensions.InternalPolicy);
// they aren't routed by the BFF either.
app.MapImportApi();
app.MapOverridesApi();

app.Run();

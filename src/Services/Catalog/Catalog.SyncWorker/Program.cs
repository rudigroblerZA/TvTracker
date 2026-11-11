using Catalog.SyncWorker.Alerts;
using Catalog.Data;
using Catalog.Embeddings;
using Catalog.SyncWorker;
using Catalog.SyncWorker.Nanogenres;
using Catalog.SyncWorker.Providers;
using Catalog.SyncWorker.Providers.Tvdb;
using Catalog.SyncWorker.Providers.Wikidata;
using Catalog.SyncWorker.Quotes;
using Catalog.SyncWorker.Settings;
using Catalog.SyncWorker.Supplement;
using Microsoft.Extensions.Options;
using TvTracker;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// The import, replay and override APIs answer only other services' internal client.
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

// Wikidata's query service, for the Wikidata provider and for the gap-fill job whatever the provider. One try per
// request, within the service's own 60 s limit: Wikimedia's 429s are waited out by the client.
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

// Catalog:Provider picks the data source: "tvmaze" (default), "wikidata" (CC0), "tvdb" (TheTVDB, with an API key) or
// "mock" for invented offline data.
var catalogProvider = CatalogSources.Configured(builder.Configuration);
if (catalogProvider == CatalogSources.Mock)
{
    builder.Services.AddSingleton<ICatalogProvider, MockCatalogProvider>();
}
else if (catalogProvider == CatalogSources.Wikidata)
{
    builder.Services.AddSingleton<ICatalogProvider, WikidataCatalogProvider>();
}
else if (catalogProvider == CatalogSources.Tvdb)
{
    // Without a key every request would be refused, so the worker says so at start instead.
    builder.Services.AddOptions<TvdbOptions>()
        .Bind(builder.Configuration.GetSection("Tvdb"))
        .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey),
            "Catalog:Provider is \"tvdb\" but Tvdb:ApiKey isn't set (the AppHost's tvdb-api-key parameter).")
        .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _) && o.BaseUrl.EndsWith('/'), "Tvdb:BaseUrl must be an absolute address ending in a slash.")
        .ValidateOnStart();
    builder.Services.AddHttpClient(TvdbClient.HttpClientName, (sp, http) =>
    {
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<TvdbOptions>>().Value.BaseUrl);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TvTracker/1.0");
    });
    builder.Services.AddSingleton<TvdbClient>();
    builder.Services.AddSingleton<ICatalogProvider, TvdbCatalogProvider>();
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
// Each can be run now from the import CLI (tvtracker-import wikidata): the job and the endpoint share its JobRun.
builder.Services.AddKeyedSingleton(WikidataJobs.Atlas, new JobRun(WikidataJobs.Atlas, "Shows whose setting and filming places are looked up."));
builder.Services.AddKeyedSingleton(WikidataJobs.Fill, new JobRun(WikidataJobs.Fill, "Shows without an overview, network, premiere date or poster, asked about on Wikidata."));
builder.Services.AddHostedService<SettingsService>();
// Gaps in shows (overview, network, premiere date, poster) filled from Wikidata.
builder.Services.AddHostedService<WikidataSupplementService>();
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
app.MapWikidataJobsApi();

app.Run();

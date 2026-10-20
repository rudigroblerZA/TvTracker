using Catalog.Data;
using Catalog.SyncWorker;
using Catalog.SyncWorker.Providers;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// The schema is created by Catalog.API; the AppHost starts this worker once that service is healthy.
builder.AddNpgsqlDbContext<CatalogContext>("catalogdb");

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowFollowedIntegrationEvent, ShowFollowedHandler>();
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
    builder.Services.AddTransient<TvMazeRateLimitHandler>();
    builder.Services.AddHttpClient<ICatalogProvider, TvMazeCatalogProvider>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<TvMazeOptions>>().Value.BaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TvTracker/1.0");
        })
        .AddHttpMessageHandler<TvMazeRateLimitHandler>();
}

builder.Services.AddScoped<CatalogSynchronizer>();
builder.Services.AddHostedService<CatalogSyncService>();

var host = builder.Build();
host.Run();

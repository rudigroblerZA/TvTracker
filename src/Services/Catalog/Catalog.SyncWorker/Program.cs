using Catalog.Data;
using Catalog.SyncWorker;
using Catalog.SyncWorker.Providers;
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

// Swap for a TMDB or TVmaze provider once the catalog source is decided.
builder.Services.AddSingleton<ICatalogProvider, MockCatalogProvider>();

builder.Services.AddScoped<CatalogSynchronizer>();
builder.Services.AddHostedService<CatalogSyncService>();

var host = builder.Build();
host.Run();

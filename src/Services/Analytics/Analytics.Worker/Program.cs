using Analytics.Data;
using Analytics.Worker;
using Analytics.Worker.IntegrationEvents;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The schema is created by Analytics.API; the AppHost starts this worker once that service is healthy.
builder.AddNpgsqlDbContext<AnalyticsContext>("analyticsdb");

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowAddedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowUpdatedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowStatusChangedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodesScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<SeasonAnnouncedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowRatedIntegrationEvent, RatingEventHandlers>()
    .AddSubscription<ShowRatingRemovedIntegrationEvent, RatingEventHandlers>()
    .AddSubscription<EpisodeRatedIntegrationEvent, RatingEventHandlers>()
    .AddSubscription<EpisodeRatingRemovedIntegrationEvent, RatingEventHandlers>()
    .AddSubscription<EpisodeWatchedIntegrationEvent, TrackingEventHandlers>()
    .AddSubscription<EpisodeUnwatchedIntegrationEvent, TrackingEventHandlers>()
    .AddSubscription<ShowFollowedIntegrationEvent, TrackingEventHandlers>()
    .AddSubscription<ShowUnfollowedIntegrationEvent, TrackingEventHandlers>()
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InsightsComputer>();
builder.Services.AddHostedService<InsightsRefresher>();

// Backfill on first start; the sync worker's internal replay endpoint.
builder.Services.AddHttpClient<CatalogReplayRequester>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"));
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogReplayRequester>());

var app = builder.Build();

// A web host only for /health: the AppHost waits for "queue bound and consuming" before starting the catalog sync worker.
app.MapDefaultEndpoints();

app.Run();

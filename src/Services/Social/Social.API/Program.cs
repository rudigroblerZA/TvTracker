using Social.API.Apis;
using Social.API.Data;
using Social.API.IntegrationEvents;
using Social.API.Services;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();
builder.AddFeatureFlags();

builder.AddNpgsqlDbContext<SocialContext>("socialdb");
builder.Services.AddDatabaseInitializer<SocialContext>();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowAddedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowUpdatedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodesScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodeScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<UserRegisteredIntegrationEvent, ProfileEventHandlers>()
    .AddSubscription<UserProfileUpdatedIntegrationEvent, ProfileEventHandlers>()
    .AddSubscription<UserProfileSnapshotIntegrationEvent, ProfileEventHandlers>()
    .AddSubscription<UserDeletedIntegrationEvent, ProfileEventHandlers>()
    .AddSubscription<EpisodeWatchedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<EpisodeUnwatchedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<ShowCompletedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<ShowFollowedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<ShowUnfollowedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<ShowRatedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<ShowRatingRemovedIntegrationEvent, ActivityEventHandlers>()
    .AddSubscription<EpisodeWatchedIntegrationEvent, UserStateHandlers>()
    .AddSubscription<EpisodeUnwatchedIntegrationEvent, UserStateHandlers>()
    .AddSubscription<ShowFollowedIntegrationEvent, UserStateHandlers>()
    .AddSubscription<ShowUnfollowedIntegrationEvent, UserStateHandlers>()
    .AddSubscription<ShowRatedIntegrationEvent, UserStateHandlers>()
    .AddSubscription<ShowRatingRemovedIntegrationEvent, UserStateHandlers>()
    .AddSubscription<TrackingSnapshotIntegrationEvent, UserStateHandlers>()
    .AddSubscription<RatingsSnapshotIntegrationEvent, UserStateHandlers>()
    .AddSubscription<ListSharedIntegrationEvent, SharedListHandlers>()
    .AddSubscription<ListUnsharedIntegrationEvent, SharedListHandlers>()
    .AddSubscription<TrackingSnapshotIntegrationEvent, SharedListHandlers>();
builder.Services.AddIntegrationEventOutbox<SocialContext>();

builder.Services.AddSingleton(TimeProvider.System);

// Backfill on first start; the sync worker's internal replay endpoint.
builder.Services.AddHttpClient<CatalogReplayRequester>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"));
builder.Services.AddHostedService(sp => sp.GetRequiredService<CatalogReplayRequester>());

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/v1/status", () => new { Service = "social-api", Status = "ok" });
app.MapSocialApi();
app.MapTogetherApi();
app.MapTonightTogetherApi();

app.Run();

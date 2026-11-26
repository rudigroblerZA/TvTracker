using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Apis;
using Ratings.API.IntegrationEvents;
using Ratings.API.Services;
using Ratings.Data;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();
builder.AddFeatureFlags();

builder.AddNpgsqlDbContext<RatingsContext>("ratingsdb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
builder.Services.AddDatabaseInitializer<RatingsContext>(baseline: "InitialCreate");

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<EpisodeWatchedIntegrationEvent, EpisodeWatchedHandler>()
    .AddSubscription<EpisodeUnwatchedIntegrationEvent, EpisodeUnwatchedHandler>()
    .AddSubscription<TrackingSnapshotIntegrationEvent, TrackingSnapshotHandler>()
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>();
builder.Services.AddIntegrationEventOutbox<RatingsContext>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<RatingsSnapshotPublisher>();
builder.Services.AddSingleton<Moderation>();
// GIFs in comments, when there's a GIPHY key (Giphy:ApiKey).
builder.Services.Configure<GiphyOptions>(builder.Configuration.GetSection("Giphy"));

// Accepted corrections are applied by the catalog sync worker.
builder.Services.AddServiceToken("Internal", IdentitySettings.InternalClient);
// Favourite-character votes are checked against the show's cast, which the catalog has.
builder.Services.AddHttpClient<CatalogLookup>(http =>
{
    http.BaseAddress = new Uri("https+http://catalog-api/");
    // A vote waits on this: a slow catalog gives way to an unchecked vote.
    http.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<CatalogSyncClient>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"))
    .AddHttpMessageHandler<ServiceTokenHandler>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "ratings-api", Status = "ok" });
app.MapRatingsApi();
app.MapCommentsApi();
app.MapCommunityApi();
app.MapModerationApi();
app.MapDuelsApi();
app.MapCharacterVotesApi();

app.MapExportApi();

app.Run();

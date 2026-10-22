using Tracking.API.Apis;
using Tracking.API.Data;
using Tracking.API.IntegrationEvents;
using Tracking.API.Services;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFeatureFlags();
builder.AddDefaultAuthentication();

builder.AddNpgsqlDbContext<TrackingContext>("trackingdb");
builder.Services.AddDatabaseInitializer<TrackingContext>();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowAddedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowUpdatedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowStatusChangedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodesScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodeScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>();
builder.Services.AddIntegrationEventOutbox<TrackingContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<UserCache>();
builder.Services.AddScoped<TrackingService>();

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

app.MapGet("/api/v1/status", () => new { Service = "tracking-api", Status = "ok" });
app.MapFollowsApi();
app.MapScheduleApi();
app.MapListsApi();
app.MapExportApi();

app.Run();

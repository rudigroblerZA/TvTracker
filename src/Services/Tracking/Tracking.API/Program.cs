using Tracking.API.Apis;
using Tracking.API.IntegrationEvents;
using Tracking.API.Services;
using Tracking.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;
using TvTracker.UserPlans;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFeatureFlags();
builder.Services.AddCopiedUserPlans<TrackingContext>();
builder.AddDefaultAuthentication();

builder.AddNpgsqlDbContext<TrackingContext>("trackingdb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
// Databases made by EnsureCreated, before migrations, have InitialCreate's schema.
builder.Services.AddDatabaseInitializer<TrackingContext>(baseline: "InitialCreate");

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowAddedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowUpdatedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<ShowStatusChangedIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodesScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<EpisodeScheduledIntegrationEvent, CatalogEventHandlers>()
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>()
    .AddSubscription<UserPlanChangedIntegrationEvent, PlanCopyHandlers<TrackingContext>>()
    .AddSubscription<UserProfileSnapshotIntegrationEvent, PlanCopyHandlers<TrackingContext>>();
builder.Services.AddIntegrationEventOutbox<TrackingContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<UserCache>();
builder.Services.AddSingleton<FollowMetrics>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddHostedService<ActivitySnapshotPublisher>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "tracking-api", Status = "ok" });
app.MapFollowsApi();
app.MapScheduleApi();
app.MapListsApi();
app.MapExportApi();

app.Run();

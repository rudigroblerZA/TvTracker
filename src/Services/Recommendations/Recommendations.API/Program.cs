using Recommendations.API.Apis;
using Recommendations.API.Data;
using Recommendations.API.IntegrationEvents;
using Recommendations.API.Services;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();

builder.AddNpgsqlDbContext<RecommendationsContext>("recommendationsdb");
builder.Services.AddDatabaseInitializer<RecommendationsContext>();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<ShowAddedIntegrationEvent, ShowFeaturesHandler>()
    .AddSubscription<ShowUpdatedIntegrationEvent, ShowFeaturesHandler>()
    .AddSubscription<ShowFollowedIntegrationEvent, UserSignalHandler>()
    .AddSubscription<ShowUnfollowedIntegrationEvent, UserSignalHandler>()
    .AddSubscription<EpisodeWatchedIntegrationEvent, UserSignalHandler>()
    .AddSubscription<ShowRatedIntegrationEvent, UserSignalHandler>()
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>();
builder.Services.AddIntegrationEventOutbox<RecommendationsContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<RecommendationEngine>();
builder.Services.AddScoped<RecommendationStore>();
builder.Services.AddHostedService<RecommendationRefresher>();

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

app.MapGet("/api/v1/status", () => new { Service = "recommendations-api", Status = "ok" });
app.MapRecommendationsApi();

app.Run();

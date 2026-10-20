using Notifications.API.Apis;
using Notifications.API.Data;
using Notifications.API.IntegrationEvents;
using Notifications.API.Services;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();

builder.AddNpgsqlDbContext<NotificationsContext>("notificationsdb");
builder.Services.AddDatabaseInitializer<NotificationsContext>();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<EpisodeAiredIntegrationEvent, CatalogNotificationHandlers>()
    .AddSubscription<SeasonAnnouncedIntegrationEvent, CatalogNotificationHandlers>()
    .AddSubscription<ShowStatusChangedIntegrationEvent, CatalogNotificationHandlers>()
    .AddSubscription<ShowFollowedIntegrationEvent, UserEventHandlers>()
    .AddSubscription<ShowUnfollowedIntegrationEvent, UserEventHandlers>()
    .AddSubscription<UserRegisteredIntegrationEvent, UserEventHandlers>()
    .AddSubscription<UserProfileUpdatedIntegrationEvent, UserEventHandlers>()
    .AddSubscription<UserDeletedIntegrationEvent, UserEventHandlers>();
builder.Services.AddIntegrationEventOutbox<NotificationsContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<NotificationFanOut>();

// Delivery channels. Each logs instead of sending until its configuration is set
// (NotificationHubs:ConnectionString, WebPush:PublicKey/PrivateKey, Email:ConnectionString/SenderAddress).
builder.Services.AddScoped<NotificationHubPush>();
builder.Services.AddScoped<IChannelSender>(sp => sp.GetRequiredService<NotificationHubPush>());
builder.Services.AddScoped<IChannelSender, WebPushSender>();
builder.Services.AddScoped<IChannelSender, EmailSender>();
builder.Services.AddHostedService<DeliveryWorker>();

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

app.MapGet("/api/v1/status", () => new { Service = "notifications-api", Status = "ok" });
app.MapNotificationsApi();

app.Run();

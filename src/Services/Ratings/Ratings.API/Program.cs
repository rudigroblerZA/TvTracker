using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Apis;
using Ratings.API.Data;
using Ratings.API.IntegrationEvents;
using Ratings.API.Services;
using TvTracker.IntegrationEvents;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();

builder.AddNpgsqlDbContext<RatingsContext>("ratingsdb");
builder.Services.AddDatabaseInitializer<RatingsContext>();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<EpisodeWatchedIntegrationEvent, EpisodeWatchedHandler>()
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>();
builder.Services.AddIntegrationEventOutbox<RatingsContext>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<RatingsSnapshotPublisher>();

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

app.MapGet("/api/v1/status", () => new { Service = "ratings-api", Status = "ok" });
app.MapRatingsApi();
app.MapCommentsApi();

app.MapGet("/api/v1/me/export", async (ClaimsPrincipal user, RatingsContext db, CancellationToken ct) =>
{
    var userId = user.GetUserId();
    return Results.Ok(new
    {
        showRatings = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId)
            .Select(r => new { r.ShowId, r.Stars, r.RatedAtUtc }).ToListAsync(ct),
        episodeRatings = await db.EpisodeRatings.AsNoTracking().Where(r => r.UserId == userId)
            .Select(r => new { r.ShowId, r.EpisodeId, r.Stars, r.RatedAtUtc }).ToListAsync(ct),
        comments = await db.Comments.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new { c.ShowId, c.EpisodeId, c.Text, c.IsSpoiler, c.CreatedAtUtc }).ToListAsync(ct),
        reactions = await db.Reactions.AsNoTracking().Where(r => r.UserId == userId)
            .Select(r => new { r.EpisodeId, r.Emoji, r.CreatedAtUtc }).ToListAsync(ct)
    });
}).RequireAuthorization();

app.Run();

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Apis;
using Ratings.API.Data;
using Ratings.API.IntegrationEvents;
using Ratings.API.Services;
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
            .Select(c => new { c.ShowId, c.EpisodeId, c.Text, c.GifId, c.IsSpoiler, c.CreatedAtUtc, c.EditedAtUtc }).ToListAsync(ct),
        reactions = await db.Reactions.AsNoTracking().Where(r => r.UserId == userId)
            .Select(r => new { r.EpisodeId, r.Emoji, r.CreatedAtUtc }).ToListAsync(ct),
        communityInsights = await db.Insights.AsNoTracking().Where(i => i.UserId == userId)
            .Select(i => new { i.ShowId, i.EpisodeId, i.Text, i.IsSpoiler, i.HiddenReason, i.CreatedAtUtc }).ToListAsync(ct),
        helpfulVotes = await db.InsightVotes.AsNoTracking().Where(v => v.UserId == userId)
            .Select(v => new { v.InsightId, v.CreatedAtUtc }).ToListAsync(ct),
        flags = await db.Flags.AsNoTracking().Where(f => f.UserId == userId)
            .Select(f => new { f.TargetKind, f.TargetId, f.Reason, f.Note, f.CreatedAtUtc }).ToListAsync(ct),
        corrections = await db.Corrections.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new { c.ShowId, c.EpisodeId, c.Field, c.SuggestedValue, c.Note, c.Status, c.CreatedAtUtc }).ToListAsync(ct),
        episodeDuels = await db.Duels.AsNoTracking().Where(d => d.UserId == userId)
            .Select(d => new { d.ShowId, d.WinnerId, d.LoserId, d.DecidedAtUtc }).ToListAsync(ct),
        characterVotes = await db.CharacterVotes.AsNoTracking().Where(v => v.UserId == userId)
            .Select(v => new { v.ShowId, v.EpisodeId, v.PersonId, v.CreatedAtUtc }).ToListAsync(ct)
    });
}).RequireAuthorization();

app.Run();

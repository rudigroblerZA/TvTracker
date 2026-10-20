using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Ratings.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Ratings.API.Apis;

public record RateRequest(int Stars);

public record RateEpisodeRequest(Guid ShowId, int Stars);

public record MyRatingDto(int Stars, DateTime RatedAtUtc);

/// <param name="Distribution">Number of ratings for 1 to 5 stars (index 0 = 1 star).</param>
public record RatingSummaryDto(double Average, int Count, IReadOnlyList<int> Distribution);

public static class RatingsApi
{
    private static readonly HybridCacheEntryOptions SummaryCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    public static IEndpointRouteBuilder MapRatingsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // Summaries are public.
        api.MapGet("/shows/{showId:guid}/ratings/summary", GetShowSummaryAsync);
        api.MapGet("/episodes/{episodeId:guid}/ratings/summary", GetEpisodeSummaryAsync);

        var mine = api.MapGroup("").RequireAuthorization();
        mine.MapGet("/shows/{showId:guid}/rating", GetMyShowRatingAsync);
        mine.MapPut("/shows/{showId:guid}/rating", RateShowAsync);
        mine.MapDelete("/shows/{showId:guid}/rating", DeleteShowRatingAsync);
        mine.MapGet("/episodes/{episodeId:guid}/rating", GetMyEpisodeRatingAsync);
        mine.MapPut("/episodes/{episodeId:guid}/rating", RateEpisodeAsync);
        mine.MapDelete("/episodes/{episodeId:guid}/rating", DeleteEpisodeRatingAsync);

        return app;
    }

    private static async Task<Ok<RatingSummaryDto>> GetShowSummaryAsync(Guid showId, RatingsContext db, HybridCache cache, CancellationToken ct)
    {
        var summary = await cache.GetOrCreateAsync(
            ShowSummaryKey(showId),
            async token => Summarize(await db.ShowRatings.Where(r => r.ShowId == showId).Select(r => r.Stars).ToListAsync(token)),
            SummaryCacheOptions,
            cancellationToken: ct);
        return TypedResults.Ok(summary);
    }

    private static async Task<Ok<RatingSummaryDto>> GetEpisodeSummaryAsync(Guid episodeId, RatingsContext db, HybridCache cache, CancellationToken ct)
    {
        var summary = await cache.GetOrCreateAsync(
            EpisodeSummaryKey(episodeId),
            async token => Summarize(await db.EpisodeRatings.Where(r => r.EpisodeId == episodeId).Select(r => r.Stars).ToListAsync(token)),
            SummaryCacheOptions,
            cancellationToken: ct);
        return TypedResults.Ok(summary);
    }

    private static async Task<Results<Ok<MyRatingDto>, NotFound>> GetMyShowRatingAsync(Guid showId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var rating = await db.ShowRatings.FindAsync([user.GetUserId(), showId], ct);
        return rating is null ? TypedResults.NotFound() : TypedResults.Ok(new MyRatingDto(rating.Stars, rating.RatedAtUtc));
    }

    private static async Task<Results<NoContent, ValidationProblem>> RateShowAsync(
        Guid showId, RateRequest request, ClaimsPrincipal user, RatingsContext db, IIntegrationEventOutbox outbox, HybridCache cache, CancellationToken ct)
    {
        if (!IsValidStars(request.Stars))
        {
            return StarsProblem();
        }

        var userId = user.GetUserId();
        var rating = await db.ShowRatings.FindAsync([userId, showId], ct);
        if (rating is null)
        {
            rating = new ShowRating { UserId = userId, ShowId = showId };
            db.ShowRatings.Add(rating);
        }
        rating.Stars = request.Stars;
        rating.RatedAtUtc = DateTime.UtcNow;

        outbox.Add(new ShowRatedIntegrationEvent(userId, showId, request.Stars));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        await cache.RemoveAsync(ShowSummaryKey(showId), ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteShowRatingAsync(Guid showId, ClaimsPrincipal user, RatingsContext db, HybridCache cache, CancellationToken ct)
    {
        var rating = await db.ShowRatings.FindAsync([user.GetUserId(), showId], ct);
        if (rating is null)
        {
            return TypedResults.NotFound();
        }

        db.ShowRatings.Remove(rating);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(ShowSummaryKey(showId), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MyRatingDto>, NotFound>> GetMyEpisodeRatingAsync(Guid episodeId, ClaimsPrincipal user, RatingsContext db, CancellationToken ct)
    {
        var rating = await db.EpisodeRatings.FindAsync([user.GetUserId(), episodeId], ct);
        return rating is null ? TypedResults.NotFound() : TypedResults.Ok(new MyRatingDto(rating.Stars, rating.RatedAtUtc));
    }

    private static async Task<Results<NoContent, ValidationProblem>> RateEpisodeAsync(
        Guid episodeId, RateEpisodeRequest request, ClaimsPrincipal user, RatingsContext db, IIntegrationEventOutbox outbox, HybridCache cache, CancellationToken ct)
    {
        if (!IsValidStars(request.Stars))
        {
            return StarsProblem();
        }

        var userId = user.GetUserId();
        var rating = await db.EpisodeRatings.FindAsync([userId, episodeId], ct);
        if (rating is null)
        {
            rating = new EpisodeRating { UserId = userId, EpisodeId = episodeId, ShowId = request.ShowId };
            db.EpisodeRatings.Add(rating);
        }
        rating.Stars = request.Stars;
        rating.RatedAtUtc = DateTime.UtcNow;

        outbox.Add(new EpisodeRatedIntegrationEvent(userId, request.ShowId, episodeId, request.Stars));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        await cache.RemoveAsync(EpisodeSummaryKey(episodeId), ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteEpisodeRatingAsync(Guid episodeId, ClaimsPrincipal user, RatingsContext db, HybridCache cache, CancellationToken ct)
    {
        var rating = await db.EpisodeRatings.FindAsync([user.GetUserId(), episodeId], ct);
        if (rating is null)
        {
            return TypedResults.NotFound();
        }

        db.EpisodeRatings.Remove(rating);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(EpisodeSummaryKey(episodeId), ct);
        return TypedResults.NoContent();
    }

    private static RatingSummaryDto Summarize(List<int> stars)
    {
        var distribution = new int[5];
        foreach (var s in stars)
        {
            distribution[s - 1]++;
        }
        var average = stars.Count == 0 ? 0 : Math.Round(stars.Average(), 2);
        return new RatingSummaryDto(average, stars.Count, distribution);
    }

    private static bool IsValidStars(int stars) => stars is >= 1 and <= 5;

    private static ValidationProblem StarsProblem() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["stars"] = ["Stars must be between 1 and 5."] });

    private static string ShowSummaryKey(Guid showId) => $"ratings:show:{showId}";

    private static string EpisodeSummaryKey(Guid episodeId) => $"ratings:episode:{episodeId}";
}

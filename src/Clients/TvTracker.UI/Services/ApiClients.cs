using TvTracker.UI.Models;

namespace TvTracker.UI.Services;

/// <summary>
/// Names of the HttpClients the shared UI uses. Each host registers them with its own base address:
/// the web app points straight at each service (Aspire service discovery),
/// the mobile app points at the Mobile BFF with a path prefix per service.
/// Base addresses end with "/" and calls use relative paths such as "api/v1/status".
/// </summary>
public static class ApiClients
{
    public const string Identity = "identity";
    public const string Catalog = "catalog";
    public const string Tracking = "tracking";
    public const string Ratings = "ratings";
    public const string Recommendations = "recommendations";
    public const string Notifications = "notifications";
    public const string Analytics = "analytics";

    public static readonly IReadOnlyList<string> All =
        [Identity, Catalog, Tracking, Ratings, Recommendations, Notifications, Analytics];
}

public sealed class IdentityClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<Profile?> GetProfileAsync(CancellationToken ct = default) =>
        GetAsync<Profile>(ApiClients.Identity, "api/v1/profile", ct, cacheForOffline: true);

    public Task<Profile?> UpdateProfileAsync(UpdateProfile update, CancellationToken ct = default) =>
        SendAsync<Profile>(ApiClients.Identity, HttpMethod.Put, "api/v1/profile", update, ct);

    public Task DeleteAccountAsync(CancellationToken ct = default) =>
        SendAsync(ApiClients.Identity, HttpMethod.Delete, "api/v1/account", null, ct);

    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var json = await GetAsync<System.Text.Json.JsonElement>(ApiClients.Identity, "api/v1/account/export", ct);
        return System.Text.Json.JsonSerializer.Serialize(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class CatalogClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<PagedResult<ShowSummary>?> SearchAsync(string? search, string? genre = null, int page = 1, CancellationToken ct = default)
    {
        var query = $"api/v1/shows?page={page}&pageSize=24";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(genre)) query += $"&genre={Uri.EscapeDataString(genre)}";
        return GetAsync<PagedResult<ShowSummary>>(ApiClients.Catalog, query, ct);
    }

    public Task<List<ShowSummary>?> GetTrendingAsync(CancellationToken ct = default) =>
        GetAsync<List<ShowSummary>>(ApiClients.Catalog, "api/v1/shows/trending", ct, cacheForOffline: true);

    public Task<ShowDetail?> GetShowAsync(Guid id, string? region = null, CancellationToken ct = default) =>
        GetAsync<ShowDetail>(ApiClients.Catalog, $"api/v1/shows/{id}?region={Uri.EscapeDataString(region ?? "US")}", ct, cacheForOffline: true);

    public Task<PersonDetail?> GetPersonAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<PersonDetail>(ApiClients.Catalog, $"api/v1/people/{id}", ct, cacheForOffline: true);

    public Task<SeasonDetail?> GetSeasonAsync(Guid showId, int number, CancellationToken ct = default) =>
        GetAsync<SeasonDetail>(ApiClients.Catalog, $"api/v1/shows/{showId}/seasons/{number}", ct, cacheForOffline: true);

    public Task<Episode?> GetEpisodeAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<Episode>(ApiClients.Catalog, $"api/v1/episodes/{id}", ct, cacheForOffline: true);

    public Task<List<string>?> GetGenresAsync(CancellationToken ct = default) =>
        GetAsync<List<string>>(ApiClients.Catalog, "api/v1/genres", ct, cacheForOffline: true);
}

public sealed class TrackingClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<List<Follow>?> GetFollowsAsync(WatchStatus? status = null, CancellationToken ct = default) =>
        GetAsync<List<Follow>>(ApiClients.Tracking, status is null ? "api/v1/follows" : $"api/v1/follows?status={status}", ct, cacheForOffline: true);

    public Task<Follow?> GetFollowAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<Follow>(ApiClients.Tracking, $"api/v1/follows/{showId}", ct, cacheForOffline: true);

    public Task<Follow?> FollowAsync(Guid showId, WatchStatus status = WatchStatus.Watching, CancellationToken ct = default) =>
        SendAsync<Follow>(ApiClients.Tracking, HttpMethod.Post, $"api/v1/follows/{showId}", new { status }, ct, queueIfOffline: true);

    public Task UnfollowAsync(Guid showId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Delete, $"api/v1/follows/{showId}", null, ct, queueIfOffline: true);

    public Task<Follow?> SetStatusAsync(Guid showId, WatchStatus status, CancellationToken ct = default) =>
        SendAsync<Follow>(ApiClients.Tracking, HttpMethod.Put, $"api/v1/follows/{showId}/status", new { status }, ct, queueIfOffline: true);

    /// <summary>Marks episodes watched. The current time is sent so offline replays keep it.</summary>
    public Task MarkWatchedAsync(MarkWatched request, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Post, "api/v1/watched",
            request with { WatchedAtUtc = request.WatchedAtUtc ?? DateTimeOffset.UtcNow }, ct, queueIfOffline: true);

    public Task UnmarkWatchedAsync(Guid episodeId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Delete,
            $"api/v1/watched/{episodeId}?changedAt={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}", null, ct, queueIfOffline: true);

    public Task<List<UpNextItem>?> GetUpNextAsync(CancellationToken ct = default) =>
        GetAsync<List<UpNextItem>>(ApiClients.Tracking, "api/v1/up-next", ct, cacheForOffline: true);

    public Task<List<CalendarItem>?> GetCalendarAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        GetAsync<List<CalendarItem>>(ApiClients.Tracking,
            $"api/v1/calendar?from={Uri.EscapeDataString(fromUtc.ToString("O"))}&to={Uri.EscapeDataString(toUtc.ToString("O"))}",
            ct, cacheForOffline: true);

    public Task<ShowProgress?> GetProgressAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<ShowProgress>(ApiClients.Tracking, $"api/v1/progress/{showId}", ct, cacheForOffline: true);

    public Task<Stats?> GetStatsAsync(CancellationToken ct = default) =>
        GetAsync<Stats>(ApiClients.Tracking, "api/v1/stats", ct, cacheForOffline: true);

    public Task<List<ListSummary>?> GetListsAsync(CancellationToken ct = default) =>
        GetAsync<List<ListSummary>>(ApiClients.Tracking, "api/v1/lists", ct, cacheForOffline: true);

    public Task<ListDetail?> GetListAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<ListDetail>(ApiClients.Tracking, $"api/v1/lists/{id}", ct, cacheForOffline: true);

    public Task<ListSummary?> CreateListAsync(string name, string? description, CancellationToken ct = default) =>
        SendAsync<ListSummary>(ApiClients.Tracking, HttpMethod.Post, "api/v1/lists", new { name, description }, ct);

    public Task UpdateListAsync(Guid id, string name, string? description, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Put, $"api/v1/lists/{id}", new { name, description }, ct, queueIfOffline: true);

    public Task DeleteListAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Delete, $"api/v1/lists/{id}", null, ct, queueIfOffline: true);

    public Task AddToListAsync(Guid listId, Guid showId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Post, $"api/v1/lists/{listId}/items", new { showId }, ct, queueIfOffline: true);

    public Task RemoveFromListAsync(Guid listId, Guid showId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Delete, $"api/v1/lists/{listId}/items/{showId}", null, ct, queueIfOffline: true);
}

public sealed class RatingsClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<RatingSummary?> GetShowSummaryAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<RatingSummary>(ApiClients.Ratings, $"api/v1/shows/{showId}/ratings/summary", ct);

    public Task<MyRating?> GetMyShowRatingAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<MyRating>(ApiClients.Ratings, $"api/v1/shows/{showId}/rating", ct);

    public Task RateShowAsync(Guid showId, int stars, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Put, $"api/v1/shows/{showId}/rating", new { stars }, ct, queueIfOffline: true);

    public Task<RatingSummary?> GetEpisodeSummaryAsync(Guid episodeId, CancellationToken ct = default) =>
        GetAsync<RatingSummary>(ApiClients.Ratings, $"api/v1/episodes/{episodeId}/ratings/summary", ct);

    public Task<MyRating?> GetMyEpisodeRatingAsync(Guid episodeId, CancellationToken ct = default) =>
        GetAsync<MyRating>(ApiClients.Ratings, $"api/v1/episodes/{episodeId}/rating", ct);

    public Task RateEpisodeAsync(Guid episodeId, Guid showId, int stars, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Put, $"api/v1/episodes/{episodeId}/rating", new { showId, stars }, ct, queueIfOffline: true);

    public Task<CommentsPage?> GetCommentsAsync(Guid episodeId, bool reveal, bool includeSpoilers, CancellationToken ct = default) =>
        GetAsync<CommentsPage>(ApiClients.Ratings,
            $"api/v1/episodes/{episodeId}/comments?reveal={reveal.ToString().ToLowerInvariant()}&includeSpoilers={includeSpoilers.ToString().ToLowerInvariant()}", ct);

    public Task<Comment?> PostCommentAsync(Guid episodeId, Guid showId, string text, bool isSpoiler, CancellationToken ct = default) =>
        SendAsync<Comment>(ApiClients.Ratings, HttpMethod.Post, $"api/v1/episodes/{episodeId}/comments", new { showId, text, isSpoiler }, ct);

    public Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Delete, $"api/v1/comments/{commentId}", null, ct);

    public Task<Reactions?> GetReactionsAsync(Guid episodeId, CancellationToken ct = default) =>
        GetAsync<Reactions>(ApiClients.Ratings, $"api/v1/episodes/{episodeId}/reactions", ct);

    public Task ReactAsync(Guid episodeId, string emoji, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Put, $"api/v1/episodes/{episodeId}/reaction", new { emoji }, ct, queueIfOffline: true);

    public Task RemoveReactionAsync(Guid episodeId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Delete, $"api/v1/episodes/{episodeId}/reaction", null, ct, queueIfOffline: true);
}

public sealed class RecommendationsClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<List<RecommendedShow>?> GetSimilarAsync(Guid showId, int limit = 10, CancellationToken ct = default) =>
        GetAsync<List<RecommendedShow>>(ApiClients.Recommendations, $"api/v1/shows/{showId}/similar?limit={limit}", ct, cacheForOffline: true);

    public Task<List<RecommendedShow>?> GetForYouAsync(int limit = 20, CancellationToken ct = default) =>
        GetAsync<List<RecommendedShow>>(ApiClients.Recommendations, $"api/v1/for-you?limit={limit}", ct, cacheForOffline: true);

    public Task DismissAsync(Guid showId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Recommendations, HttpMethod.Post, $"api/v1/dismissals/{showId}", null, ct, queueIfOffline: true);
}

/// <summary>Insights for signed-in users (the API requires a token).</summary>
public sealed class AnalyticsClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<ShowInsights?> GetShowInsightsAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<ShowInsights>(ApiClients.Analytics, $"api/v1/shows/{showId}/insights", ct, cacheForOffline: true);

    public Task<List<PremiereItem>?> GetPremieresAsync(int days = 60, string sort = "date", CancellationToken ct = default) =>
        GetAsync<List<PremiereItem>>(ApiClients.Analytics, $"api/v1/insights/premieres?days={days}&sort={sort}", ct, cacheForOffline: true);

    public Task<List<TopEpisodeItem>?> GetTopEpisodesAsync(string? genre = null, int limit = 20, CancellationToken ct = default)
    {
        var url = $"api/v1/insights/top-episodes?limit={limit}";
        if (!string.IsNullOrWhiteSpace(genre)) url += $"&genre={Uri.EscapeDataString(genre)}";
        return GetAsync<List<TopEpisodeItem>>(ApiClients.Analytics, url, ct, cacheForOffline: true);
    }

    /// <param name="list">"hidden-gems", "most-consistent", "rollercoasters" or "best-finales".</param>
    public Task<List<DiscoveryItem>?> GetDiscoveryAsync(string list, string? genre = null, int limit = 20, CancellationToken ct = default)
    {
        var url = $"api/v1/insights/discover/{list}?limit={limit}";
        if (!string.IsNullOrWhiteSpace(genre)) url += $"&genre={Uri.EscapeDataString(genre)}";
        return GetAsync<List<DiscoveryItem>>(ApiClients.Analytics, url, ct, cacheForOffline: true);
    }

    public Task<PersonInsights?> GetPersonInsightsAsync(Guid personId, CancellationToken ct = default) =>
        GetAsync<PersonInsights>(ApiClients.Analytics, $"api/v1/people/{personId}/insights", ct, cacheForOffline: true);

    public Task<Taste?> GetTasteAsync(CancellationToken ct = default) =>
        GetAsync<Taste>(ApiClients.Analytics, "api/v1/me/taste", ct, cacheForOffline: true);
}

public sealed class NotificationsClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<NotificationPreferences?> GetPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<NotificationPreferences>(ApiClients.Notifications, "api/v1/preferences", ct);

    public Task<NotificationPreferences?> UpdatePreferencesAsync(UpdateNotificationPreferences update, CancellationToken ct = default) =>
        SendAsync<NotificationPreferences>(ApiClients.Notifications, HttpMethod.Put, "api/v1/preferences", update, ct);

    public Task<ShowNotificationPreference?> GetShowPreferenceAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<ShowNotificationPreference>(ApiClients.Notifications, $"api/v1/shows/{showId}/preferences", ct);

    public Task SetShowMutedAsync(Guid showId, bool muted, CancellationToken ct = default) =>
        SendAsync(ApiClients.Notifications, HttpMethod.Put, $"api/v1/shows/{showId}/preferences", new { muted }, ct);

    public Task<RegisteredDevice?> RegisterDeviceAsync(RegisterDevice device, CancellationToken ct = default) =>
        SendAsync<RegisteredDevice>(ApiClients.Notifications, HttpMethod.Post, "api/v1/devices", device, ct);

    public Task<List<RegisteredDevice>?> GetDevicesAsync(CancellationToken ct = default) =>
        GetAsync<List<RegisteredDevice>>(ApiClients.Notifications, "api/v1/devices", ct);

    public Task RemoveDeviceAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(ApiClients.Notifications, HttpMethod.Delete, $"api/v1/devices/{id}", null, ct);

    public Task<InboxPage?> GetInboxAsync(bool unreadOnly = false, CancellationToken ct = default) =>
        GetAsync<InboxPage>(ApiClients.Notifications, $"api/v1/inbox?unreadOnly={unreadOnly.ToString().ToLowerInvariant()}", ct, cacheForOffline: true);

    public Task MarkReadAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(ApiClients.Notifications, HttpMethod.Post, $"api/v1/inbox/{id}/read", null, ct, queueIfOffline: true);

    public Task MarkAllReadAsync(CancellationToken ct = default) =>
        SendAsync(ApiClients.Notifications, HttpMethod.Post, "api/v1/inbox/read-all", null, ct, queueIfOffline: true);

    public async Task<string?> GetWebPushPublicKeyAsync(CancellationToken ct = default) =>
        (await GetAsync<WebPushKey>(ApiClients.Notifications, "api/v1/webpush/public-key", ct))?.PublicKey;

    private sealed record WebPushKey(string PublicKey);
}

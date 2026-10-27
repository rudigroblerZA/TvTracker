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
    public const string Social = "social";

    public static readonly IReadOnlyList<string> All =
        [Identity, Catalog, Tracking, Ratings, Recommendations, Notifications, Analytics, Social];
}

public sealed class IdentityClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<Profile?> GetProfileAsync(CancellationToken ct = default) =>
        GetAsync<Profile>(ApiClients.Identity, "api/v1/profile", ct, cacheForOffline: true);

    public Task<Profile?> UpdateProfileAsync(UpdateProfile update, CancellationToken ct = default) =>
        SendAsync<Profile>(ApiClients.Identity, HttpMethod.Put, "api/v1/profile", update, ct);

    public Task DeleteAccountAsync(CancellationToken ct = default) =>
        SendAsync(ApiClients.Identity, HttpMethod.Delete, "api/v1/account", null, ct);

    /// <summary>The feature flags, for admins; null for anyone else.</summary>
    public Task<List<FeatureFlagSetting>?> GetFeatureFlagsAsync(CancellationToken ct = default) =>
        GetAsync<List<FeatureFlagSetting>>(ApiClients.Identity, "api/v1/admin/flags", ct);

    /// <summary>Turns a flag on or off for everyone, or (null) back to what flagd.json says.</summary>
    public Task<FeatureFlagSetting?> SetFeatureFlagAsync(string key, bool? on, CancellationToken ct = default) =>
        SendAsync<FeatureFlagSetting>(ApiClients.Identity, HttpMethod.Put, $"api/v1/admin/flags/{Uri.EscapeDataString(key)}", new { On = on }, ct);

    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var json = await GetAsync<System.Text.Json.JsonElement>(ApiClients.Identity, "api/v1/account/export", ct);
        return System.Text.Json.JsonSerializer.Serialize(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class CatalogClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    /// <param name="tag">A show tag key (see <see cref="Components.ShowTagOptions"/>), e.g. "bingeable".</param>
    public Task<PagedResult<ShowSummary>?> SearchAsync(string? search, string? genre = null, int page = 1, string? tag = null,
        string? nanogenre = null, CancellationToken ct = default)
    {
        var query = $"api/v1/shows?page={page}&pageSize=24";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(genre)) query += $"&genre={Uri.EscapeDataString(genre)}";
        if (!string.IsNullOrWhiteSpace(tag)) query += $"&tags={Uri.EscapeDataString(tag)}";
        if (!string.IsNullOrWhiteSpace(nanogenre)) query += $"&nanogenre={Uri.EscapeDataString(nanogenre)}";
        return GetAsync<PagedResult<ShowSummary>>(ApiClients.Catalog, query, ct);
    }

    /// <summary>Every nanogenre the catalog has discovered, largest first.</summary>
    public Task<List<NanogenreSummary>?> GetNanogenresAsync(CancellationToken ct = default) =>
        GetAsync<List<NanogenreSummary>>(ApiClients.Catalog, "api/v1/nanogenres", ct, cacheForOffline: true);

    /// <summary>A nanogenre and its shows; null when the slug no longer exists.</summary>
    public Task<NanogenreDetail?> GetNanogenreAsync(string slug, CancellationToken ct = default) =>
        GetAsync<NanogenreDetail>(ApiClients.Catalog, $"api/v1/nanogenres/{Uri.EscapeDataString(slug)}", ct, cacheForOffline: true);

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

    public Task<List<UniverseSummary>?> GetUniversesAsync(CancellationToken ct = default) =>
        GetAsync<List<UniverseSummary>>(ApiClients.Catalog, "api/v1/universes", ct, cacheForOffline: true);

    /// <summary>The universe the show is in; null when it shares characters with no other show.</summary>
    public Task<Universe?> GetUniverseAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<Universe>(ApiClients.Catalog, $"api/v1/shows/{showId}/universe", ct, cacheForOffline: true);

    /// <summary>How well each show fits a mood, 0-1 within the set (Catalog's semantic-search vectors).</summary>
    public Task<MoodResponse?> GetMoodFitsAsync(string mood, IReadOnlyCollection<Guid> showIds, CancellationToken ct = default) =>
        SendAsync<MoodResponse>(ApiClients.Catalog, HttpMethod.Post, "api/v1/shows/mood", new { mood, showIds }, ct);

    public Task<WatchOrder?> GetWatchOrderAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<WatchOrder>(ApiClients.Catalog, $"api/v1/shows/{showId}/universe/watch-order", ct, cacheForOffline: true);

    /// <summary>Catalog shows whose title contains the term, for suggest-as-you-type pickers; no provider import.</summary>
    public Task<List<ShowSummary>?> SuggestAsync(string term, CancellationToken ct = default) =>
        GetAsync<List<ShowSummary>>(ApiClients.Catalog, $"api/v1/shows/suggest?q={Uri.EscapeDataString(term)}", ct);

    /// <param name="lean">0-100, how much like <paramref name="a"/>.</param>
    public Task<Blend?> GetBlendAsync(Guid a, Guid b, int lean = 50, CancellationToken ct = default) =>
        GetAsync<Blend>(ApiClients.Catalog, $"api/v1/shows/blend?a={a}&b={b}&lean={lean}", ct);
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

    /// <summary>Shows being watched or on the watchlist, with their next unwatched aired episodes, for Tonight's plan.</summary>
    public Task<List<TonightShow>?> GetTonightQueueAsync(int perShow = 6, CancellationToken ct = default) =>
        GetAsync<List<TonightShow>>(ApiClients.Tracking, $"api/v1/tonight/queue?perShow={perShow}", ct);

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

    /// <param name="shared">Share it on Social; null leaves it as it is.</param>
    public Task UpdateListAsync(Guid id, string name, string? description, bool? shared = null, CancellationToken ct = default) =>
        SendAsync(ApiClients.Tracking, HttpMethod.Put, $"api/v1/lists/{id}", new { name, description, shared }, ct, queueIfOffline: true);

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

    /// <summary>Flags a comment ("comment") or community insight ("insight"); null when it's gone.</summary>
    public Task<FlagResult?> FlagAsync(string kind, Guid id, string reason, string? note, CancellationToken ct = default) =>
        SendAsync<FlagResult>(ApiClients.Ratings, HttpMethod.Post,
            kind == "comment" ? $"api/v1/comments/{id}/flag" : $"api/v1/community-insights/{id}/flag", new { Reason = reason, Note = note }, ct);

    /// <summary>A show's community insights, or one episode's.</summary>
    public Task<CommunityInsights?> GetCommunityInsightsAsync(Guid showId, Guid? episodeId, CancellationToken ct = default) =>
        GetAsync<CommunityInsights>(ApiClients.Ratings,
            $"api/v1/shows/{showId}/community-insights{(episodeId is { } e ? $"?episodeId={e}" : "")}", ct);

    public Task<CommunityInsight?> PostCommunityInsightAsync(Guid showId, Guid? episodeId, string text, bool isSpoiler, CancellationToken ct = default) =>
        SendAsync<CommunityInsight>(ApiClients.Ratings, HttpMethod.Post, $"api/v1/shows/{showId}/community-insights",
            new { EpisodeId = episodeId, Text = text, IsSpoiler = isSpoiler }, ct);

    public Task DeleteCommunityInsightAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Delete, $"api/v1/community-insights/{id}", null, ct);

    public Task SetHelpfulAsync(Guid id, bool helpful, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, helpful ? HttpMethod.Put : HttpMethod.Delete, $"api/v1/community-insights/{id}/helpful", null, ct);

    public Task<Correction?> SuggestCorrectionAsync(Guid showId, Guid? episodeId, string field, string? currentValue, string suggestedValue,
        string? note, string? context, CancellationToken ct = default) =>
        SendAsync<Correction>(ApiClients.Ratings, HttpMethod.Post, "api/v1/corrections",
            new { ShowId = showId, EpisodeId = episodeId, Field = field, CurrentValue = currentValue, SuggestedValue = suggestedValue, Note = note, Context = context }, ct);

    public Task<List<Correction>?> GetMyCorrectionsAsync(CancellationToken ct = default) =>
        GetAsync<List<Correction>>(ApiClients.Ratings, "api/v1/me/corrections", ct);

    /// <summary>The moderation queue, for moderators; null for anyone else.</summary>
    public Task<ModerationQueue?> GetModerationQueueAsync(CancellationToken ct = default) =>
        GetAsync<ModerationQueue>(ApiClients.Ratings, "api/v1/moderation", ct);

    /// <summary>Restores (shows again) or removes a flagged or held post.</summary>
    public Task SettlePostAsync(string kind, Guid id, bool restore, CancellationToken ct = default) =>
        SendAsync(ApiClients.Ratings, HttpMethod.Post, $"api/v1/moderation/posts/{kind}/{id}/{(restore ? "restore" : "remove")}", null, ct);

    /// <summary>Accepts (applying <paramref name="value"/>, or the suggestion), rejects or reverts a correction.</summary>
    public Task<Correction?> ReviewCorrectionAsync(Guid id, string action, string? value = null, CancellationToken ct = default) =>
        SendAsync<Correction>(ApiClients.Ratings, HttpMethod.Post, $"api/v1/moderation/corrections/{id}/{action}",
            action == "accept" ? new { Value = value } : null, ct);

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

    public Task<List<DnaTwin>?> GetDnaTwinsAsync(Guid showId, int limit = 6, CancellationToken ct = default) =>
        GetAsync<List<DnaTwin>>(ApiClients.Recommendations, $"api/v1/shows/{showId}/dna-twins?limit={limit}", ct, cacheForOffline: true);

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

    /// <summary>Null for anyone without a fingerprint (fewer than two shows created or developed).</summary>
    public Task<CreatorFingerprint?> GetCreatorFingerprintAsync(Guid personId, CancellationToken ct = default) =>
        GetAsync<CreatorFingerprint>(ApiClients.Analytics, $"api/v1/people/{personId}/fingerprint", ct, cacheForOffline: true);

    public Task<PersonInsights?> GetPersonInsightsAsync(Guid personId, CancellationToken ct = default) =>
        GetAsync<PersonInsights>(ApiClients.Analytics, $"api/v1/people/{personId}/insights", ct, cacheForOffline: true);

    /// <summary>Tags of up to 100 shows; shows without insights yet are missing from the result.</summary>
    public Task<Dictionary<Guid, List<ShowTag>>?> GetTagsAsync(IEnumerable<Guid> showIds, CancellationToken ct = default) =>
        GetAsync<Dictionary<Guid, List<ShowTag>>>(ApiClients.Analytics, $"api/v1/insights/tags?ids={string.Join(',', showIds)}", ct);

    /// <summary>"Will you finish it?" for every show the user is part-way through.</summary>
    public Task<List<FinishOutlook>?> GetFinishOutlooksAsync(CancellationToken ct = default) =>
        GetAsync<List<FinishOutlook>>(ApiClients.Analytics, "api/v1/me/finish-outlook", ct);

    /// <summary>The TV time machine: this week in TV history, or one date's week.</summary>
    /// <param name="date">A past date to travel to; null for this week in every past year.</param>
    /// <param name="timeZone">The user's profile time zone, whose calendar days make up the weeks; null for UTC.</param>
    public Task<TimeMachine?> GetTimeMachineAsync(DateOnly? date, string? timeZone, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (date is { } d) query.Add($"date={d:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(timeZone)) query.Add($"tz={Uri.EscapeDataString(timeZone)}");
        return GetAsync<TimeMachine>(ApiClients.Analytics, "api/v1/insights/time-machine" + (query.Count > 0 ? "?" + string.Join('&', query) : ""), ct);
    }

    /// <summary>"Will you finish it?" for one show; null when the user hasn't started it or is caught up.</summary>
    public Task<FinishOutlook?> GetFinishOutlookAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<FinishOutlook>(ApiClients.Analytics, $"api/v1/shows/{showId}/finish-outlook", ct);

    /// <summary>Shows where actors from the user's favourites work together again (the reunion radar).</summary>
    public Task<ReunionRadar?> GetReunionsAsync(CancellationToken ct = default) =>
        GetAsync<ReunionRadar>(ApiClients.Analytics, "api/v1/me/reunions", ct, cacheForOffline: true);

    public Task<Taste?> GetTasteAsync(CancellationToken ct = default) =>
        GetAsync<Taste>(ApiClients.Analytics, "api/v1/me/taste", ct, cacheForOffline: true);

    /// <summary>"Previously on…" for one show, cut to <paramref name="minutes"/>; null when the user hasn't watched it, or has seen it all and nothing more is coming.</summary>
    public Task<Refresher?> GetRefresherAsync(Guid showId, int minutes = 120, CancellationToken ct = default) =>
        GetAsync<Refresher>(ApiClients.Analytics, $"api/v1/shows/{showId}/refresher?minutes={minutes}", ct);

    /// <summary>Followed shows the user has had a long break from, with something to go back to.</summary>
    public Task<List<Refresher>?> GetRefreshersAsync(CancellationToken ct = default) =>
        GetAsync<List<Refresher>>(ApiClients.Analytics, "api/v1/me/refreshers", ct);

    /// <summary>The catch-up plan for one show; null when no new season is due soon or the user is caught up.</summary>
    /// <param name="cut">Plan the weeks along the essential cut, when the show has one.</param>
    public Task<CatchUpPlan?> GetCatchUpAsync(Guid showId, bool cut = false, CancellationToken ct = default) =>
        GetAsync<CatchUpPlan>(ApiClients.Analytics, $"api/v1/shows/{showId}/catch-up?cut={(cut ? "true" : "false")}", ct);

    /// <summary>Followed shows with a new season coming and episodes to catch up on first.</summary>
    public Task<List<CatchUpPlan>?> GetCatchUpsAsync(CancellationToken ct = default) =>
        GetAsync<List<CatchUpPlan>>(ApiClients.Analytics, "api/v1/me/catch-up", ct);

    /// <summary>"Is it worth going back?"; null unless the user dropped the show.</summary>
    public Task<Comeback?> GetComebackAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<Comeback>(ApiClients.Analytics, $"api/v1/shows/{showId}/comeback", ct);

    /// <summary>The user's backlog, triaged: followed shows not started, and dropped ones.</summary>
    public Task<Backlog?> GetBacklogAsync(CancellationToken ct = default) =>
        GetAsync<Backlog>(ApiClients.Analytics, "api/v1/me/backlog", ct, cacheForOffline: true);

    /// <summary>Finished shows to watch start to end in <paramref name="hours"/>.</summary>
    public Task<TimeOff?> GetTimeOffAsync(int hours, CancellationToken ct = default) =>
        GetAsync<TimeOff>(ApiClients.Analytics, $"api/v1/me/time-off?hours={hours}", ct);

    /// <param name="level">"light", "lean" or "essentials".</param>
    public Task<EssentialCut?> GetEssentialCutAsync(Guid showId, string level = "lean", CancellationToken ct = default) =>
        GetAsync<EssentialCut>(ApiClients.Analytics, $"api/v1/shows/{showId}/essential-cut?level={level}", ct, cacheForOffline: true);
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

public sealed class SocialClient(IHttpClientFactory f, IAccessTokenProvider t, IOfflineStore o, ConnectionState c, ClientInfo i) : ApiClientBase(f, t, o, c, i)
{
    public Task<MySocial?> GetMeAsync(CancellationToken ct = default) =>
        GetAsync<MySocial>(ApiClients.Social, "api/v1/me/social", ct);

    public Task<SocialSettings?> UpdateSettingsAsync(SocialSettings settings, CancellationToken ct = default) =>
        SendAsync<SocialSettings>(ApiClients.Social, HttpMethod.Put, "api/v1/me/social/settings", settings, ct);

    /// <summary>The newest activity of the people the user follows; pass the last page's NextBefore for older items.</summary>
    public Task<Feed?> GetFeedAsync(DateTime? before = null, CancellationToken ct = default) =>
        GetAsync<Feed>(ApiClients.Social, before is { } b ? $"api/v1/feed?before={Uri.EscapeDataString(b.ToString("O"))}" : "api/v1/feed", ct);

    public Task<List<SocialPerson>?> SearchAsync(string query, CancellationToken ct = default) =>
        GetAsync<List<SocialPerson>>(ApiClients.Social, $"api/v1/people?q={Uri.EscapeDataString(query)}", ct);

    public Task<SocialPersonPage?> GetPersonAsync(string userId, CancellationToken ct = default) =>
        GetAsync<SocialPersonPage>(ApiClients.Social, $"api/v1/people/{Uri.EscapeDataString(userId)}", ct);

    public Task<List<SocialPerson>?> GetFollowersAsync(string userId, CancellationToken ct = default) =>
        GetAsync<List<SocialPerson>>(ApiClients.Social, $"api/v1/people/{Uri.EscapeDataString(userId)}/followers", ct);

    public Task<List<SocialPerson>?> GetFollowingAsync(string userId, CancellationToken ct = default) =>
        GetAsync<List<SocialPerson>>(ApiClients.Social, $"api/v1/people/{Uri.EscapeDataString(userId)}/following", ct);

    /// <summary>Following a private profile makes a request: the result says which.</summary>
    public Task<FollowState?> FollowAsync(string userId, CancellationToken ct = default) =>
        SendAsync<FollowState>(ApiClients.Social, HttpMethod.Post, $"api/v1/people/{Uri.EscapeDataString(userId)}/follow", null, ct);

    public Task<List<SocialPerson>?> GetFollowRequestsAsync(CancellationToken ct = default) =>
        GetAsync<List<SocialPerson>>(ApiClients.Social, "api/v1/me/follow-requests", ct);

    public Task ApproveRequestAsync(string userId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Social, HttpMethod.Post, $"api/v1/me/follow-requests/{Uri.EscapeDataString(userId)}/approve", null, ct);

    public Task DeclineRequestAsync(string userId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Social, HttpMethod.Delete, $"api/v1/me/follow-requests/{Uri.EscapeDataString(userId)}", null, ct);

    public Task<List<SocialPerson>?> GetBlocksAsync(CancellationToken ct = default) =>
        GetAsync<List<SocialPerson>>(ApiClients.Social, "api/v1/me/blocks", ct);

    public Task BlockAsync(string userId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Social, HttpMethod.Post, $"api/v1/people/{Uri.EscapeDataString(userId)}/block", null, ct);

    public Task UnblockAsync(string userId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Social, HttpMethod.Delete, $"api/v1/people/{Uri.EscapeDataString(userId)}/block", null, ct);

    public Task<SharedList?> GetSharedListAsync(string userId, Guid listId, CancellationToken ct = default) =>
        GetAsync<SharedList>(ApiClients.Social, $"api/v1/people/{Uri.EscapeDataString(userId)}/lists/{listId}", ct);

    /// <summary>The people the user follows (and can see) who follow, watch or rated this show.</summary>
    public Task<FriendsOnShow?> GetFriendsOnShowAsync(Guid showId, CancellationToken ct = default) =>
        GetAsync<FriendsOnShow>(ApiClients.Social, $"api/v1/shows/{showId}/friends", ct);

    /// <summary>Shows the user and someone else both follow or have started; null when the user can't see their activity.</summary>
    public Task<Together?> GetTogetherAsync(string userId, CancellationToken ct = default) =>
        GetAsync<Together>(ApiClients.Social, $"api/v1/people/{Uri.EscapeDataString(userId)}/together", ct);

    /// <summary>The queue for an evening with someone else; null when the user can't see their activity.</summary>
    public Task<TonightTogether?> GetTonightTogetherAsync(string userId, int perShow = 8, CancellationToken ct = default) =>
        GetAsync<TonightTogether>(ApiClients.Social, $"api/v1/people/{Uri.EscapeDataString(userId)}/tonight?perShow={perShow}", ct);

    public Task UnfollowAsync(string userId, CancellationToken ct = default) =>
        SendAsync(ApiClients.Social, HttpMethod.Delete, $"api/v1/people/{Uri.EscapeDataString(userId)}/follow", null, ct);
}

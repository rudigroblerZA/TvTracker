using System.Security.Claims;
using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="Logo">The service's icon as a <c>data:</c> URI; null when it couldn't be fetched.</param>
/// <param name="Networks">The catalog networks whose shows are on it.</param>
public record ServiceDto(string Key, string Name, decimal? MonthlyUsd, string? Note, string? Logo, IReadOnlyList<string> Networks);

public record ServiceDirectoryDto(string PricesAsOf, string PricesSource, IReadOnlyList<ServiceDto> Services);

/// <param name="Allowed">Whether the caller's plan lets them keep services (<see cref="PlanLimits.TrackServices"/>).</param>
public record MyServicesDto(string Plan, bool Allowed, IReadOnlyList<string> Services);

public record SetMyServicesRequest(IReadOnlyList<string> Services);

/// <param name="Minutes">Watched in the last <see cref="ServicesApi.WindowDays"/> days, bulk marks left out.</param>
/// <param name="CostPerHour">The month's price over those hours; null when the price is unknown or free, or nothing was watched.</param>
/// <param name="Verdict">"value", "pricey", "quiet" (nothing watched, but followed shows are coming) or "pause".</param>
/// <param name="Watched">The shows watched on it in the window, most minutes first.</param>
/// <param name="Coming">Followed shows with episodes airing on it in the next <see cref="ServicesApi.WindowDays"/> days.</param>
public record ServiceUseDto(string Key, string Name, decimal? MonthlyUsd, int Minutes, decimal? CostPerHour, string Verdict,
    IReadOnlyList<ServiceShowDto> Watched, IReadOnlyList<ServiceShowDto> Coming);

/// <param name="Minutes">Watched in the window (for <see cref="ServiceUseDto.Watched"/>).</param>
/// <param name="Episodes">Episodes watched, or airing in the window (for <see cref="ServiceUseDto.Coming"/>).</param>
/// <param name="NextAirsAtUtc">The next episode, for coming shows.</param>
public record ServiceShowDto(Guid ShowId, string Title, int Minutes, int Episodes, DateTimeOffset? NextAirsAtUtc);

/// <param name="MonthlyUsd">The month's total for the services with a known price.</param>
/// <param name="UnknownPrices">Services in the total without a known price.</param>
/// <param name="Missing">Services the caller doesn't have, with followed shows coming on them.</param>
public record ServicesReportDto(int WindowDays, decimal MonthlyUsd, int UnknownPrices, int Minutes, IReadOnlyList<ServiceUseDto> Services,
    IReadOnlyList<ServiceUseDto> Missing, string PricesAsOf);

/// <summary>
/// The streaming services behind the networks (<see cref="NetworkServices"/>) and, on Plus and Family
/// (<see cref="PlanLimits.TrackServices"/>), the services a user pays for and what each costs per hour they watch.
/// <list type="bullet">
/// <item><b>Logos</b> are each service's site icon, fetched here once a week and sent as <c>data:</c> URIs, so a browser
/// or the phone never asks a third party for them (and needs no route to this service for an image).</item>
/// <item><b>Prices</b> are estimates: US list prices of the cheapest plan, checked by hand (<see cref="NetworkServices.PricesAsOf"/>).</item>
/// <item><b>The report</b> looks at the last 30 days of watching and the next 30 days of the user's followed shows.
/// Bulk marks (8+ of a show's episodes at one moment, as Year in TV) say when episodes were marked, not watched, so they're
/// left out. A service with nothing watched and nothing coming is one to pause; over $2 an hour (a rental's price) is pricey.</item>
/// </list>
/// </summary>
public static class ServicesApi
{
    public const int WindowDays = 30;
    private const int BulkMark = 8;
    private const decimal PriceyPerHour = 2m;
    private const int MaxShowsListed = 5;

    private static readonly TimeSpan LogoCacheTime = TimeSpan.FromDays(7);

    public static void MapServicesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/networks/services", GetDirectoryAsync).RequireFeature(FeatureFlags.YourServices);
        api.MapGet("/me/services", GetMineAsync).RequireFeature(FeatureFlags.YourServices);
        api.MapPut("/me/services", SetMineAsync).RequireFeature(FeatureFlags.YourServices);
        api.MapGet("/me/services/report", GetReportAsync).RequireFeature(FeatureFlags.YourServices);

        // The only data here the user gave us (everything else is a copy of other services'), for Identity's data export.
        api.MapGet("/me/export", async (ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct) =>
            user.FindFirst("sub")?.Value is not { } userId
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    services = await db.UserServices.AsNoTracking().Where(s => s.UserId == userId)
                        .Select(s => new { s.ServiceKey, s.AddedAtUtc }).ToListAsync(ct)
                }));
    }

    private static async Task<Ok<ServiceDirectoryDto>> GetDirectoryAsync(HybridCache cache, IHttpClientFactory http, CancellationToken ct)
    {
        var logos = await cache.GetOrCreateAsync("service-logos", async token => await FetchLogosAsync(http, token),
            new HybridCacheEntryOptions { Expiration = LogoCacheTime, LocalCacheExpiration = LogoCacheTime }, cancellationToken: ct);
        var services = NetworkServices.All
            .Select(s => new ServiceDto(s.Key, s.Name, s.MonthlyUsd, s.Note, logos.GetValueOrDefault(s.Key), NetworkServices.NetworksOf(s.Key).ToList()))
            .ToList();
        return TypedResults.Ok(new ServiceDirectoryDto(NetworkServices.PricesAsOf, NetworkServices.PricesSource, services));
    }

    /// <summary>
    /// Each service's 64-pixel icon, through Google's favicon service (it finds icons sites keep in different places).
    /// Only the directory's own domains are asked for, so this can't be used to fetch anything else.
    /// </summary>
    private static async Task<Dictionary<string, string>> FetchLogosAsync(IHttpClientFactory http, CancellationToken ct)
    {
        var client = http.CreateClient("logos");
        var fetched = await Task.WhenAll(NetworkServices.All.Select(async s =>
        {
            try
            {
                using var response = await client.GetAsync($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(s.Domain)}&sz=64", ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not { } type || !type.StartsWith("image/"))
                {
                    return (s.Key, Logo: (string?)null);
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                return (s.Key, Logo: $"data:{type};base64,{Convert.ToBase64String(bytes)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (s.Key, Logo: null);
            }
        }));
        return fetched.Where(f => f.Logo is not null).ToDictionary(f => f.Key, f => f.Logo!);
    }

    private static async Task<Results<Ok<MyServicesDto>, UnauthorizedHttpResult>> GetMineAsync(
        ClaimsPrincipal user, AnalyticsContext db, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var plan = await PlanOfAsync(db, userId, ct);
        var services = await db.UserServices.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.ServiceKey).ToListAsync(ct);
        return TypedResults.Ok(new MyServicesDto(plan, Plans.Limits(plan).TrackServices, services));
    }

    /// <summary>Replaces the caller's services. A plan without it keeps what it saved before, but can't change it.</summary>
    private static async Task<Results<Ok<MyServicesDto>, UnauthorizedHttpResult, ValidationProblem, ProblemHttpResult>> SetMineAsync(
        SetMyServicesRequest request, ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var plan = await PlanOfAsync(db, userId, ct);
        if (!Plans.Limits(plan).TrackServices)
        {
            return PlanLimit(plan);
        }
        var keys = request.Services.Distinct().ToList();
        if (keys.FirstOrDefault(k => NetworkServices.Get(k) is null) is { } unknown)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["services"] = [$"Unknown service: {unknown}."] });
        }

        var existing = await db.UserServices.Where(s => s.UserId == userId).ToListAsync(ct);
        db.UserServices.RemoveRange(existing.Where(s => !keys.Contains(s.ServiceKey)));
        var now = time.GetUtcNow().UtcDateTime;
        db.UserServices.AddRange(keys.Where(k => existing.All(s => s.ServiceKey != k))
            .Select(k => new UserService { UserId = userId, ServiceKey = k, AddedAtUtc = now }));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new MyServicesDto(plan, true, keys));
    }

    private static async Task<Results<Ok<ServicesReportDto>, UnauthorizedHttpResult, ProblemHttpResult>> GetReportAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var plan = await PlanOfAsync(db, userId, ct);
        if (!Plans.Limits(plan).TrackServices)
        {
            return PlanLimit(plan);
        }

        var now = time.GetUtcNow();
        var from = now.UtcDateTime.AddDays(-WindowDays);
        var until = now.AddDays(WindowDays);
        var owned = await db.UserServices.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.ServiceKey).ToListAsync(ct);

        var watches = await db.Watches.AsNoTracking()
            .Where(w => w.UserId == userId && w.WatchedAtUtc >= from)
            .Join(db.Episodes, w => w.EpisodeId, e => e.EpisodeId, (w, e) => new { w.ShowId, w.WatchedAtUtc, e.RuntimeMinutes })
            .ToListAsync(ct);
        var bulk = watches.GroupBy(w => (w.ShowId, w.WatchedAtUtc)).Where(g => g.Count() >= BulkMark).Select(g => g.Key).ToHashSet();
        var watched = watches.Where(w => !bulk.Contains((w.ShowId, w.WatchedAtUtc)))
            .GroupBy(w => w.ShowId)
            .ToDictionary(g => g.Key, g => (Minutes: g.Sum(w => w.RuntimeMinutes ?? 0), Episodes: g.Count()));

        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var coming = await db.Episodes.AsNoTracking()
            .Where(e => followed.Contains(e.ShowId) && e.AirsAtUtc > now && e.AirsAtUtc <= until)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, Episodes = g.Count(), Next = g.Min(e => e.AirsAtUtc) })
            .ToDictionaryAsync(g => g.ShowId, ct);

        var showIds = watched.Keys.Concat(coming.Keys).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId))
            .Select(s => new { s.ShowId, s.Title, s.Network })
            .ToDictionaryAsync(s => s.ShowId, ct);
        var serviceOf = shows.Values
            .Select(s => (s.ShowId, Service: NetworkServices.ForNetwork(s.Network)))
            .Where(s => s.Service is not null)
            .ToDictionary(s => s.ShowId, s => s.Service!.Key);

        ServiceUseDto Use(NetworkService service)
        {
            var mine = serviceOf.Where(s => s.Value == service.Key).Select(s => s.Key).ToList();
            var watchedHere = mine.Where(watched.ContainsKey)
                .Select(id => new ServiceShowDto(id, shows[id].Title, watched[id].Minutes, watched[id].Episodes, null))
                .OrderByDescending(s => s.Minutes).ToList();
            var comingHere = mine.Where(coming.ContainsKey)
                .Select(id => new ServiceShowDto(id, shows[id].Title, 0, coming[id].Episodes, coming[id].Next))
                .OrderBy(s => s.NextAirsAtUtc).ToList();
            var minutes = watchedHere.Sum(s => s.Minutes);
            decimal? perHour = service.MonthlyUsd is > 0 && minutes > 0 ? Math.Round(service.MonthlyUsd.Value / (minutes / 60m), 2) : null;
            var verdict = minutes == 0
                ? comingHere.Count > 0 ? "quiet" : "pause"
                : perHour > PriceyPerHour ? "pricey" : "value";
            return new ServiceUseDto(service.Key, service.Name, service.MonthlyUsd, minutes, perHour, verdict,
                watchedHere.Take(MaxShowsListed).ToList(), comingHere.Take(MaxShowsListed).ToList());
        }

        var mineUse = owned.Select(NetworkServices.Get).OfType<NetworkService>().Select(Use)
            .OrderByDescending(s => s.Minutes).ToList();
        var missing = serviceOf.Values.Distinct().Except(owned)
            .Select(NetworkServices.Get).OfType<NetworkService>().Select(Use)
            .Where(s => s.Coming.Count > 0)
            .OrderByDescending(s => s.Coming.Count).ToList();

        return TypedResults.Ok(new ServicesReportDto(
            WindowDays,
            mineUse.Sum(s => s.MonthlyUsd ?? 0),
            mineUse.Count(s => s.MonthlyUsd is null),
            mineUse.Sum(s => s.Minutes),
            mineUse,
            missing,
            NetworkServices.PricesAsOf));
    }

    private static async Task<string> PlanOfAsync(AnalyticsContext db, string userId, CancellationToken ct) =>
        await db.Plans.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.Plan).FirstOrDefaultAsync(ct) ?? Plans.Free;

    /// <summary>403 typed <c>plan-limit</c>, as Tracking answers, so clients offer the upgrade.</summary>
    private static ProblemHttpResult PlanLimit(string plan) =>
        TypedResults.Problem($"Keeping your services is part of {Plans.Name(Plans.Plus)}. {Plans.Name(plan)} accounts can see the report cards' prices.",
            statusCode: StatusCodes.Status403Forbidden, title: "Not on your plan", type: "plan-limit");
}

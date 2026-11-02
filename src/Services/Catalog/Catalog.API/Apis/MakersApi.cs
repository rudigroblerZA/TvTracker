using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="ShowId">Null for a show that isn't in the catalog; the UI offers a search, which imports it.</param>
/// <param name="Status">Upcoming with no <paramref name="From"/> is in development: the provider keeps abandoned projects
/// that way for years, so it isn't counted as new or coming.</param>
/// <param name="New">Premiered within the last year, or dated and still to come.</param>
public record MadeShowDto(Guid? ShowId, string ExternalId, string Title, string? PosterUrl, int? From, int? To, ShowStatus Status,
    double? Rating, double Popularity, bool New);

/// <param name="Roles">What they were on this show: "Creator" and/or "Developer".</param>
/// <param name="CreditsPending">Their credits haven't been fetched yet, so their other shows are unknown for now.</param>
/// <param name="Shows">Their other shows, newest first, at most <see cref="MakersApi.ShowsPerMaker"/>.</param>
public record MakerDto(Guid PersonId, string Name, string? PhotoUrl, IReadOnlyList<string> Roles, bool CreditsPending,
    IReadOnlyList<MadeShowDto> Shows);

public record ShowMakersDto(Guid ShowId, IReadOnlyList<MakerDto> Makers);

/// <param name="Weight">How much it's loved (Analytics' <c>/me/loved</c>).</param>
public record LovedShowInput(Guid ShowId, double Weight);

/// <param name="Known">Every show the caller rated, follows or watched: left out of the picks.</param>
public record MakersForYouRequest(IReadOnlyList<LovedShowInput> Loved, IReadOnlyList<Guid> Known);

/// <param name="Love">The summed love of the caller's shows they made.</param>
/// <param name="OtherShows">Their shows the caller doesn't know yet.</param>
/// <param name="Pending">Their credits aren't fetched yet, so <paramref name="OtherShows"/> is unknown, not none.</param>
public record YourMakerDto(Guid PersonId, string Name, string? PhotoUrl, double Love, IReadOnlyList<ShowRefDto> LovedShows, int OtherShows,
    bool Pending = false);

/// <param name="Because">The loved show the caller knows them for.</param>
public record MakerCreditDto(Guid PersonId, string Name, ShowRefDto Because);

public record MakerPickDto(MadeShowDto Show, IReadOnlyList<MakerCreditDto> By, double Score);

/// <param name="MakersPending">Makers of loved shows whose credits aren't fetched yet.</param>
/// <param name="New">From the caller's makers, premiered within the last year or still to come, soonest-ahead first.</param>
/// <param name="Picks">Their other shows the caller doesn't know, best first.</param>
public record MakersForYouDto(int LovedShows, int MakersPending, IReadOnlyList<YourMakerDto> Makers, IReadOnlyList<MakerPickDto> New,
    IReadOnlyList<MakerPickDto> Picks);

/// <summary>
/// From the makers: the shows a show's creators and developers made, and, for the caller, the people behind the shows
/// they love and those people's other work. From the provider's crew credits (<see cref="PersonMadeShow"/>, fetched with
/// each maker's career), so shows outside the catalog count, each marked so the page can offer to import it.
/// <list type="bullet">
/// <item><b>Creators and developers only:</b> an executive producer's credits follow a production company, not a voice,
/// as the creator fingerprint decided.</item>
/// <item><b>Nothing of the caller's is kept:</b> the page sends the loved and known shows (from Analytics' <c>/me/loved</c>)
/// and gets the picks back.</item>
/// </list>
/// </summary>
public static class MakersApi
{
    public const int ShowsPerMaker = 8;
    private const int MaxLoved = 300;
    private const int MaxKnown = 5000;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly string[] MakerRoles = ["Creator", "Developer"];

    public static void MapMakersApi(this RouteGroupBuilder api)
    {
        api.MapGet("/shows/{id:guid}/makers", GetShowAsync).RequireFeature(FeatureFlags.Makers);
        api.MapPost("/makers/for-you", ForYouAsync)
            .RequireAuthorization()
            .RequireRateLimiting(SearchRateLimiting.Policy)
            .RequireFeature(FeatureFlags.MakersForYou);
    }

    private static async Task<Results<Ok<ShowMakersDto>, NotFound>> GetShowAsync(Guid id, CatalogContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        var result = await cache.GetOrCreateAsync($"makers:{id}", async token => await BuildShowAsync(db, id, time.GetUtcNow(), token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    private static async Task<ShowMakersDto?> BuildShowAsync(CatalogContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var show = await db.Shows.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.Id, s.Source, s.ExternalId }).SingleOrDefaultAsync(ct);
        if (show is null)
        {
            return null;
        }

        var makers = await db.Creatives.AsNoTracking()
            .Where(c => c.ShowId == id && (c.Roles.Contains("Creator") || c.Roles.Contains("Developer")))
            .OrderBy(c => c.Order)
            .Select(c => new { c.PersonId, c.Roles, c.Person.Name, c.Person.PhotoUrl, c.Person.CareerRefreshedAtUtc })
            .ToListAsync(ct);
        var made = await MadeAsync(db, makers.Select(m => m.PersonId).ToList(), ct);
        var catalogIds = await CatalogIdsAsync(db, made.SelectMany(m => m).Select(m => (show.Source, m.ShowExternalId)), ct);

        return new ShowMakersDto(id, makers.Select(m => new MakerDto(m.PersonId, m.Name, m.PhotoUrl, m.Roles.Intersect(MakerRoles).ToList(),
                m.CareerRefreshedAtUtc is null,
                made[m.PersonId].Where(s => s.ShowExternalId != show.ExternalId)
                    .Select(s => ToDto(s, show.Source, catalogIds, now))
                    .OrderBy(MakersRanking.Order).ThenBy(MakersRanking.OrderYear)
                    .Take(ShowsPerMaker)
                    .ToList()))
            .ToList());
    }

    private static async Task<Results<Ok<MakersForYouDto>, ValidationProblem>> ForYouAsync(MakersForYouRequest request, CatalogContext db,
        TimeProvider time, CancellationToken ct)
    {
        if (request.Loved is null || request.Known is not { Count: <= MaxKnown })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [$"Send the loved shows and at most {MaxKnown} known ones."]
            });
        }

        // The most loved count most, so a long history is cut to them rather than refused.
        var loved = request.Loved.OrderByDescending(l => l.Weight).Take(MaxLoved).ToList();
        var lovedIds = loved.Select(l => l.ShowId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => lovedIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.Title, s.Source }, ct);
        var credits = await db.Creatives.AsNoTracking()
            .Where(c => lovedIds.Contains(c.ShowId) && (c.Roles.Contains("Creator") || c.Roles.Contains("Developer")))
            .Select(c => new { c.ShowId, c.PersonId, c.Person.Name, c.Person.PhotoUrl, c.Person.CareerRefreshedAtUtc })
            .ToListAsync(ct);
        var made = await MadeAsync(db, credits.Select(c => c.PersonId).Distinct().ToList(), ct);
        // A maker's credits come from the provider of the show they're credited on.
        var sources = credits.GroupBy(c => c.PersonId).ToDictionary(g => g.Key, g => shows.TryGetValue(g.First().ShowId, out var s) ? s.Source : "");
        var catalogIds = await CatalogIdsAsync(db, made.SelectMany(m => m.Select(s => (sources[m.Key], s.ShowExternalId))), ct);
        var now = time.GetUtcNow();

        var makers = credits.GroupBy(c => c.PersonId)
            .Select(g => new MakersRanking.Maker(g.Key, g.First().Name, g.First().PhotoUrl, g.First().CareerRefreshedAtUtc is null,
                g.Select(c => c.ShowId).Distinct().ToList(), made[g.Key].Select(s => ToDto(s, sources[g.Key], catalogIds, now)).ToList()))
            .ToList();
        return TypedResults.Ok(MakersRanking.Build(loved, request.Known.ToHashSet(), shows.ToDictionary(s => s.Key, s => s.Value.Title), makers));
    }

    private static async Task<ILookup<Guid, PersonMadeShow>> MadeAsync(CatalogContext db, List<Guid> personIds, CancellationToken ct) =>
        (await db.MadeShows.AsNoTracking().Where(m => personIds.Contains(m.PersonId)).ToListAsync(ct)).ToLookup(m => m.PersonId);

    /// <summary>
    /// Catalog ids of the provider's shows, matched when read, so a show imported after the credits links up.
    /// </summary>
    private static async Task<Dictionary<(string Source, string ExternalId), Guid>> CatalogIdsAsync(CatalogContext db,
        IEnumerable<(string Source, string ExternalId)> keys, CancellationToken ct)
    {
        var wanted = keys.ToHashSet();
        var sources = wanted.Select(k => k.Source).Distinct().ToList();
        var ids = wanted.Select(k => k.ExternalId).Distinct().ToList();
        return (await db.Shows.AsNoTracking().Where(s => sources.Contains(s.Source) && ids.Contains(s.ExternalId))
                .Select(s => new { s.Source, s.ExternalId, s.Id })
                .ToListAsync(ct))
            .Where(s => wanted.Contains((s.Source, s.ExternalId)))
            .ToDictionary(s => (s.Source, s.ExternalId), s => s.Id);
    }

    private static MadeShowDto ToDto(PersonMadeShow s, string source, IReadOnlyDictionary<(string, string), Guid> catalogIds, DateTimeOffset now) =>
        new(catalogIds.TryGetValue((source, s.ShowExternalId), out var showId) ? showId : null, s.ShowExternalId, s.Title, s.PosterUrl,
            s.Premiered?.Year, s.Ended?.Year, s.Status, s.Rating, s.Popularity,
            s.Status == ShowStatus.Upcoming ? s.Premiered is not null : s.Premiered >= DateOnly.FromDateTime(now.UtcDateTime.AddYears(-1)));
}

/// <summary>
/// From the makers you love, as a pure function of the caller's loved and known shows and the makers' credits.
/// <list type="bullet">
/// <item><b>A maker's love</b> is the summed weight of the caller's loved shows they created or developed, so someone behind
/// two favourites counts more than someone behind one.</item>
/// <item><b>A pick's score</b> is its makers' love (summed, when two of them made it) × its rating / 10, an unrated show
/// counting as <see cref="UnratedRating"/>, so the score reads as "how much of your love, how good".</item>
/// <item><b>Left out:</b> the caller's known shows (rated, followed, watched), the loved shows themselves, and undated
/// projects in development.</item>
/// </list>
/// </summary>
internal static class MakersRanking
{
    public const double UnratedRating = 7.0;
    private const int MaxMakers = 12;
    private const int MaxNew = 10;
    private const int MaxPicks = 20;

    /// <summary>
    /// A maker's shows in reading order: dated ones still to come (soonest first, by <see cref="OrderYear"/>), then the
    /// rest newest first, and undated projects in development last.
    /// </summary>
    public static int Order(MadeShowDto s) => s.Status == ShowStatus.Upcoming ? s.From is null ? 2 : 0 : 1;

    /// <summary>
    /// Within <see cref="Order"/>: coming shows by year ascending, the others descending.
    /// </summary>
    public static int OrderYear(MadeShowDto s) => s.Status == ShowStatus.Upcoming ? s.From ?? 0 : -(s.From ?? 0);

    public sealed record Maker(Guid PersonId, string Name, string? PhotoUrl, bool Pending, IReadOnlyList<Guid> LovedShowIds,
        IReadOnlyList<MadeShowDto> Made);

    public static MakersForYouDto Build(IReadOnlyList<LovedShowInput> loved, IReadOnlySet<Guid> known, IReadOnlyDictionary<Guid, string> titles,
        IReadOnlyList<Maker> makers)
    {
        var weights = loved.GroupBy(l => l.ShowId).ToDictionary(g => g.Key, g => g.Max(l => l.Weight));
        var love = makers.ToDictionary(m => m.PersonId, m => m.LovedShowIds.Sum(id => weights.GetValueOrDefault(id)));

        // The loved show each maker is best known to the caller for: their most loved, then the alphabet.
        ShowRefDto Because(Maker m) => m.LovedShowIds
            .OrderByDescending(id => weights.GetValueOrDefault(id)).ThenBy(id => titles.GetValueOrDefault(id), StringComparer.Ordinal)
            .Select(id => new ShowRefDto(id, titles.GetValueOrDefault(id) ?? ""))
            .First();

        // Undated projects in development can't be watched, so they're neither picks nor counted as other shows.
        bool Unknown(MadeShowDto s) => Order(s) != 2 && (s.ShowId is not { } id || !known.Contains(id) && !weights.ContainsKey(id));

        var candidates = makers
            .SelectMany(m => m.Made.Where(Unknown).Select(s => (Maker: m, Show: s)))
            .GroupBy(x => x.Show.ExternalId)
            .Select(g =>
            {
                var show = g.First().Show;
                var by = g.Select(x => x.Maker).DistinctBy(m => m.PersonId).OrderByDescending(m => love[m.PersonId]).ToList();
                var score = by.Sum(m => love[m.PersonId]) * (show.Rating ?? UnratedRating) / 10;
                return new MakerPickDto(show, by.Select(m => new MakerCreditDto(m.PersonId, m.Name, Because(m))).ToList(), Math.Round(score, 3));
            })
            .ToList();

        return new MakersForYouDto(
            weights.Count,
            makers.Count(m => m.Pending),
            makers.Where(m => love[m.PersonId] > 0)
                .OrderByDescending(m => love[m.PersonId]).ThenBy(m => m.Name, StringComparer.Ordinal)
                .Take(MaxMakers)
                .Select(m => new YourMakerDto(m.PersonId, m.Name, m.PhotoUrl, Math.Round(love[m.PersonId], 2),
                    m.LovedShowIds.Select(id => new ShowRefDto(id, titles.GetValueOrDefault(id) ?? "")).OrderBy(s => s.Title, StringComparer.Ordinal).ToList(),
                    m.Made.Count(Unknown), m.Pending))
                .ToList(),
            candidates.Where(c => c.Show.New)
                .OrderBy(c => Order(c.Show)).ThenBy(c => OrderYear(c.Show))
                .ThenByDescending(c => c.Score)
                .Take(MaxNew)
                .ToList(),
            candidates.Where(c => !c.Show.New)
                .OrderByDescending(c => c.Score).ThenByDescending(c => c.Show.Popularity).ThenBy(c => c.Show.Title, StringComparer.Ordinal)
                .Take(MaxPicks)
                .ToList());
    }
}

using Analytics.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Analytics.API.Apis;

/// <param name="LovedShows">How many of the user's shows the radar looked from.</param>
/// <param name="ActorsPending">Actors of those shows whose roles elsewhere haven't been fetched from the provider yet.</param>
public record ReunionRadarDto(int LovedShows, int ActorsPending, IReadOnlyList<ReunionDto> Reunions);

/// <param name="Show">Where they work together again.</param>
/// <param name="From">The user's show they were in together.</param>
/// <param name="IsNew">Premiering soon or in the last year: what the radar is for.</param>
public record ReunionDto(ReunionShowDto Show, ReunionSourceDto From, IReadOnlyList<ReunionPersonDto> People, double Score, bool IsNew);

/// <param name="ShowId">The catalog's id, when the show is in the catalog; else it can be found through search.</param>
public record ReunionShowDto(string ExternalId, Guid? ShowId, string Title, string? PosterUrl, ShowStatus Status, DateOnly? Premiered, double? Rating);

public record ReunionSourceDto(Guid ShowId, string Title, string? PosterUrl);

/// <param name="CharacterThen">Who they played in the user's show.</param>
/// <param name="CharacterNow">Who they play in the other show.</param>
public record ReunionPersonDto(Guid PersonId, string Name, string? CharacterThen, string? CharacterNow, bool Voice);

/// <summary>
/// Reunion radar: from the shows the user loves, the other shows where two or more of their actors work together again,
/// best first. Computed on request from the user's own ratings, follows and watches, and the actors' roles across the
/// provider (main casts only), so it needs no minimum counts: nobody else's data is involved.
/// <list type="bullet">
/// <item><b>Loved:</b> rated 4 or 5 stars, or followed or watched and not rated lower (see <see cref="LoveWeight"/>).</item>
/// <item><b>Score:</b> the loved show's weight times the actors' billing in it, so the leads of a favourite count most.</item>
/// <item><b>Not reunions:</b> a show where any of them plays the same character (a spin-off or revival: that's a
/// universe), shows the user already follows, rated or watched, and roles as themselves.</item>
/// </list>
/// </summary>
public static partial class ReunionsApi
{
    private const int MaxReunions = 40;
    private const int MinActors = 2;

    /// <summary>A role billed this far down counts half as much as the lead: weight 1 / (1 + order / 3).</summary>
    private const double BillingHalfLife = 3;

    private const int MinWatchedEpisodes = 3;

    public static void MapReunionsApi(this RouteGroupBuilder api) =>
        api.MapGet("/me/reunions", GetRadarAsync).RequireFeature(FeatureFlags.ReunionRadar);

    private static async Task<Results<Ok<ReunionRadarDto>, UnauthorizedHttpResult>> GetRadarAsync(
        ClaimsPrincipal user, AnalyticsContext db, TimeProvider time, CancellationToken ct)
    {
        if (user.FindFirst("sub")?.Value is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var stars = await db.ShowRatings.AsNoTracking().Where(r => r.UserId == userId).ToDictionaryAsync(r => r.ShowId, r => r.Stars, ct);
        var followed = await db.Follows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.ShowId).ToListAsync(ct);
        var watched = await db.Watches.AsNoTracking().Where(w => w.UserId == userId)
            .GroupBy(w => w.ShowId).Select(g => new { ShowId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(w => w.ShowId, w => w.Count, ct);

        var known = stars.Keys.Concat(followed).Concat(watched.Keys).ToHashSet();
        var loved = known
            .Select(id => (ShowId: id, Weight: LoveWeight(stars.GetValueOrDefault(id), followed.Contains(id), watched.GetValueOrDefault(id))))
            .Where(x => x.Weight > 0)
            .ToDictionary(x => x.ShowId, x => x.Weight);
        var lovedIds = loved.Keys.ToList();

        var shows = await db.Shows.AsNoTracking().Where(s => lovedIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);
        var cast = (await db.Cast.AsNoTracking().Where(c => lovedIds.Contains(c.ShowId) && c.PersonId != null).ToListAsync(ct))
            .Where(c => !IsSelfRole(c.Character, c.PersonName))
            .ToList();
        var personIds = cast.Select(c => c.PersonId!.Value).Distinct().ToList();
        var credits = (await db.CastCredits.AsNoTracking().Where(c => personIds.Contains(c.PersonId)).ToListAsync(ct))
            .ToLookup(c => c.PersonId);

        var reunions = new List<ReunionDto>();
        foreach (var (showId, weight) in loved)
        {
            if (!shows.TryGetValue(showId, out var source))
            {
                continue;
            }

            var together = cast
                .Where(c => c.ShowId == showId)
                .SelectMany(member => credits[member.PersonId!.Value]
                    .Where(credit => credit.ShowId != showId && (credit.ShowId is not { } id || !known.Contains(id)))
                    .Select(credit => (Member: member, Credit: credit)))
                .GroupBy(x => x.Credit.ShowExternalId);

            foreach (var group in together)
            {
                var people = group.DistinctBy(x => x.Member.PersonId).ToList();
                if (people.Count < MinActors
                    || people.Any(x => x.Credit.Character is { } now && x.Member.Character is { } then && SameCharacter(then, now)))
                {
                    continue;
                }

                var show = people[0].Credit;
                reunions.Add(new ReunionDto(
                    new ReunionShowDto(show.ShowExternalId, show.ShowId, show.Title, show.PosterUrl, show.Status, show.Premiered, show.Rating),
                    new ReunionSourceDto(source.ShowId, source.Title, source.PosterUrl),
                    people.OrderBy(x => x.Member.Order)
                        .Select(x => new ReunionPersonDto(x.Member.PersonId!.Value, x.Member.PersonName, x.Member.Character, x.Credit.Character, x.Credit.Voice))
                        .ToList(),
                    Math.Round(weight * people.Sum(x => 1 / (1 + x.Member.Order / BillingHalfLife)), 2),
                    IsNew(show, time.GetUtcNow())));
            }
        }

        // A show reached from several loved shows is listed once, from the one with the strongest reunion.
        var best = reunions
            .GroupBy(r => r.Show.ExternalId)
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.People.Count)
            .Take(MaxReunions)
            .ToList();

        var pending = personIds.Count(id => !credits[id].Any());
        return TypedResults.Ok(new ReunionRadarDto(shows.Count, pending, best));
    }

    /// <summary>
    /// How much a show counts as loved: 5 stars 1, 4 stars 0.8, followed or watched (at least
    /// <see cref="MinWatchedEpisodes"/> episodes) and unrated 0.5; 3 stars or fewer, not at all.
    /// </summary>
    private static double LoveWeight(int stars, bool followed, int watchedEpisodes) => stars switch
    {
        5 => 1,
        4 => 0.8,
        > 0 => 0,
        _ => followed || watchedEpisodes >= MinWatchedEpisodes ? 0.5 : 0
    };

    /// <summary>Premiering soon, or premiered within the last year.</summary>
    private static bool IsNew(PersonCastCreditCopy show, DateTimeOffset now) =>
        show.Status == ShowStatus.Upcoming || show.Premiered >= DateOnly.FromDateTime(now.UtcDateTime.AddYears(-1));

    // Character matching, by the same rules as Catalog.API's Universes (which links shows by shared characters).

    private static readonly HashSet<string> Titles =
    [
        "dr", "doctor", "mr", "mrs", "ms", "miss", "mister", "detective", "det", "captain", "capt", "agent", "special",
        "officer", "sgt", "sergeant", "lt", "lieutenant", "chief", "general", "president", "sir", "lady", "lord", "professor",
        "prof", "judge", "father", "sister", "nurse", "deputy", "sheriff", "commander", "colonel", "major", "the", "young", "older",
        "admiral", "director"
    ];

    private static readonly HashSet<string> SelfRoles =
    [
        "himself", "herself", "themselves", "self", "narrator", "host", "presenter", "various", "voice", "voices",
        "additional voices", "guest", "cameo", "co-host", "judge", "announcer"
    ];

    private static bool IsSelfRole(string? character, string actor) =>
        character is null
        || SelfRoles.Contains(character.Trim().ToLowerInvariant())
        || string.Equals(Normalize(character), Normalize(actor), StringComparison.Ordinal);

    /// <summary>
    /// Any alias ("Frank Castle / The Punisher") of one matches any alias of the other, ignoring titles, quoted
    /// nicknames and punctuation; the shorter may be part of the longer when it has two words or more.
    /// </summary>
    private static bool SameCharacter(string a, string b)
    {
        foreach (var x in Aliases(a))
        {
            foreach (var y in Aliases(b))
            {
                var (small, large) = x.Count <= y.Count ? (x, y) : (y, x);
                if (small.Count > 0 && (small.SetEquals(large) || small.Count >= 2 && small.IsSubsetOf(large)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<HashSet<string>> Aliases(string character) =>
        Quoted().Replace(character, " ").Split('/')
            .Select(name => Words().Matches(name.ToLowerInvariant()).Select(m => m.Value).Where(w => !Titles.Contains(w)).ToHashSet());

    private static string Normalize(string name) => string.Join(' ', Words().Matches(name.ToLowerInvariant()).Select(m => m.Value));

    [GeneratedRegex("\"[^\"]*\"|“[^”]*”|\\([^)]*\\)")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}

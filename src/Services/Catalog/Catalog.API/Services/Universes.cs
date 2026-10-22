using System.Text.RegularExpressions;
using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.API.Services;

/// <summary>A character the same actor plays in two shows.</summary>
public sealed record SharedCharacter(Guid PersonId, string PersonName, string? PhotoUrl, string Character);

/// <summary>Two shows linked by the characters they share.</summary>
public sealed record UniverseLink(Guid A, Guid B, IReadOnlyList<SharedCharacter> Characters);

/// <summary>Shows connected by shared characters (directly or through each other), and the links between them.</summary>
public sealed record Universe(IReadOnlyList<Guid> Shows, IReadOnlyList<UniverseLink> Links);

/// <summary>Every universe in the catalog, and which universe each show is in.</summary>
public sealed record UniverseMap(IReadOnlyList<Universe> Universes, IReadOnlyDictionary<Guid, int> UniverseOfShow);

/// <summary>
/// Shared universes: spin-offs, sequels, revivals and crossovers, found from the cast. TVmaze gives each show its own
/// character ids, even for the same character in a spin-off, so a link is the same actor playing a character with
/// the same name in two scripted or animated shows ("Matt Murdock / Daredevil" in Marvel's Daredevil and "Matt
/// Murdock" in Daredevil: Born Again). Shows linked directly or through each other form a universe.
/// <list type="bullet">
/// <item>Unscripted shows are left out: hosts, judges and crews ("Master Carpenter") recur across a channel's shows.</item>
/// <item>Self roles ("Himself", "Narrator", or the actor's own name) don't count.</item>
/// <item>A link needs two shared characters, or one with a full name ("Mon Mothma", not "Mason"), so a common
/// one-word name played by the same actor in two unrelated shows doesn't join them.</item>
/// </list>
/// Rebuilt from the database at most every 30 minutes; a few thousand shows take well under a second.
/// </summary>
public sealed partial class Universes(IServiceScopeFactory scopeFactory, HybridCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private static readonly string[] ScriptedKinds = ["scripted", "animation"];

    private static readonly HashSet<string> Titles =
    [
        "dr", "doctor", "mr", "mrs", "ms", "miss", "mister", "detective", "det", "captain", "capt", "agent", "special",
        "officer", "sgt", "sergeant", "lt", "lieutenant", "chief", "general", "president", "sir", "lady", "lord", "professor",
        "prof", "judge", "father", "sister", "nurse", "deputy", "sheriff", "commander", "colonel", "major", "the", "young", "older"
    ];

    private static readonly HashSet<string> SelfRoles =
    [
        "himself", "herself", "themselves", "self", "narrator", "host", "presenter", "various", "voice", "voices",
        "additional voices", "guest", "cameo", "co-host", "judge", "announcer"
    ];

    public Task<UniverseMap> GetAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync("universes:v1", async token => await BuildAsync(token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct).AsTask();

    private async Task<UniverseMap> BuildAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();

        // Only actors credited in two or more scripted shows can link anything.
        var scripted = db.Shows.Where(s => s.Keywords.Any(k => ScriptedKinds.Contains(k))).Select(s => s.Id);
        var credits = await db.Cast.AsNoTracking()
            .Where(c => c.Character != null && scripted.Contains(c.ShowId))
            .Where(c => db.Cast.Where(o => o.PersonId == c.PersonId && scripted.Contains(o.ShowId)).Select(o => o.ShowId).Distinct().Count() > 1)
            .Select(c => new { c.ShowId, c.PersonId, c.Person.Name, c.Person.PhotoUrl, Character = c.Character! })
            .ToListAsync(ct);

        var shared = new Dictionary<(Guid, Guid), List<SharedCharacter>>();
        foreach (var person in credits.GroupBy(c => c.PersonId))
        {
            var list = person.Where(c => !IsSelfRole(c.Character, c.Name)).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var (a, b) = (list[i], list[j]);
                    if (a.ShowId == b.ShowId || !SameCharacter(a.Character, b.Character))
                    {
                        continue;
                    }

                    var key = a.ShowId.CompareTo(b.ShowId) < 0 ? (a.ShowId, b.ShowId) : (b.ShowId, a.ShowId);
                    if (!shared.TryGetValue(key, out var characters))
                    {
                        shared[key] = characters = [];
                    }
                    if (characters.All(c => c.PersonId != a.PersonId))
                    {
                        // The shorter name is usually the plain one ("Matt Murdock", not "Matt Murdock / Daredevil").
                        characters.Add(new SharedCharacter(a.PersonId, a.Name, a.PhotoUrl,
                            a.Character.Length <= b.Character.Length ? a.Character : b.Character));
                    }
                }
            }
        }

        var links = shared
            .Where(s => s.Value.Count >= 2 || s.Value.Any(c => Tokens(c.Character).Count >= 2))
            .Select(s => new UniverseLink(s.Key.Item1, s.Key.Item2, s.Value))
            .ToList();

        // Connected components: shows linked directly or through each other.
        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        foreach (var link in links)
        {
            parent.TryAdd(link.A, link.A);
            parent.TryAdd(link.B, link.B);
            parent[Find(link.A)] = Find(link.B);
        }

        var universes = parent.Keys
            .GroupBy(Find)
            .Select(g => g.ToHashSet())
            .Select(members => new Universe(members.ToList(), links.Where(l => members.Contains(l.A)).ToList()))
            .OrderByDescending(u => u.Shows.Count)
            .ToList();
        var index = new Dictionary<Guid, int>();
        for (var i = 0; i < universes.Count; i++)
        {
            foreach (var show in universes[i].Shows)
            {
                index[show] = i;
            }
        }
        return new UniverseMap(universes, index);
    }

    private static bool IsSelfRole(string character, string actor) =>
        SelfRoles.Contains(character.Trim().ToLowerInvariant())
        || character.Contains("(voice)", StringComparison.OrdinalIgnoreCase) && character.Length < 12
        || string.Equals(Normalize(character), Normalize(actor), StringComparison.Ordinal);

    /// <summary>
    /// Whether two character names are the same character: any alias ("Frank Castle / The Punisher") of one matches
    /// any alias of the other, ignoring titles, nicknames in quotes and punctuation. One name may be the other's
    /// fuller form ("Frasier Crane" and "Dr. Frasier Crane"), as long as the shorter has two words or more.
    /// </summary>
    internal static bool SameCharacter(string a, string b)
    {
        foreach (var x in Aliases(a))
        {
            foreach (var y in Aliases(b))
            {
                var (small, large) = x.Count <= y.Count ? (x, y) : (y, x);
                if (small.Count == 0)
                {
                    continue;
                }
                if (small.SetEquals(large) || small.Count >= 2 && small.IsSubsetOf(large))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<HashSet<string>> Aliases(string character) =>
        Quoted().Replace(character, " ").Split('/').Select(Tokens);

    private static HashSet<string> Tokens(string name) =>
        Words().Matches(name.ToLowerInvariant()).Select(m => m.Value).Where(w => !Titles.Contains(w)).ToHashSet();

    private static string Normalize(string name) => string.Join(' ', Words().Matches(name.ToLowerInvariant()).Select(m => m.Value));

    [GeneratedRegex("\"[^\"]*\"|“[^”]*”|\\([^)]*\\)")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.API.Services;

/// <summary>One show's episode in a crossover event.</summary>
public sealed record CrossoverPart(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Title, DateTimeOffset AirsAtUtc);

/// <summary>A crossover event across a universe's shows, its parts in air order.</summary>
/// <param name="Id">Stable for the event (its universe, name and year), so an alert for it is sent once.</param>
/// <param name="UniverseId">The universe's hub show, which addresses it.</param>
public sealed record Crossover(Guid Id, string Name, Guid UniverseId, string UniverseName, IReadOnlyList<CrossoverPart> Parts)
{
    public DateTimeOffset StartsAtUtc => Parts[0].AirsAtUtc;
}

/// <summary>
/// Crossover events: two or more of a universe's shows airing episodes with the same title, give or take a part marker
/// ("Infection, Part I" on Chicago Fire, "Infection, Part II" on Chicago Med), within <see cref="Window"/> of each
/// other. Airing the same night isn't enough, since One Chicago's shows air back to back every week. Used by the watch
/// order and, for what's coming, by crossover alerts and Up Next.
/// </summary>
public sealed partial class Crossovers(Universes universes, IServiceScopeFactory scopeFactory, HybridCache cache, TimeProvider time)
{
    /// <summary>A crossover event's parts air within a week of each other, usually the same night.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>How far ahead <see cref="UpcomingAsync"/> looks.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(30);

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase) { "tba", "tbd", "untitled", "pilot" };

    /// <summary>
    /// Crossover events with a part still to air, starting within <see cref="Horizon"/>, soonest first. Rebuilt at most
    /// every half hour: one episode query per universe.
    /// </summary>
    public Task<List<Crossover>> UpcomingAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync("crossovers:upcoming:v1", async token => await FindUpcomingAsync(token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor }, cancellationToken: ct).AsTask();

    private async Task<List<Crossover>> FindUpcomingAsync(CancellationToken ct)
    {
        var map = await universes.GetAsync(ct);
        var now = time.GetUtcNow();
        var (from, to) = (now - Window, now + Horizon + Window);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var found = new List<Crossover>();
        foreach (var universe in map.Universes.Where(u => u.Shows.Count > 1))
        {
            var members = await db.Shows.AsNoTracking().Where(s => universe.Shows.Contains(s.Id)).ToListAsync(ct);
            var shows = members.ToDictionary(s => s.Id);
            var episodes = await db.Episodes.AsNoTracking()
                .Where(e => universe.Shows.Contains(e.ShowId) && e.SeasonNumber > 0 && e.AirsAtUtc >= from && e.AirsAtUtc <= to)
                .OrderBy(e => e.AirsAtUtc).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .Select(e => new { e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title, AirsAtUtc = e.AirsAtUtc!.Value })
                .ToListAsync(ct);
            var labels = Label(episodes.Select(e => (e.ShowId, e.Title, e.AirsAtUtc)).ToList());

            var hub = Catalog.API.Apis.UniverseApi.Hub(members, universe.Links);
            var universeName = Catalog.API.Apis.UniverseApi.Name(members, hub);
            foreach (var group in Groups(labels, episodes.Select(e => e.AirsAtUtc).ToList()))
            {
                if (group.Select(i => episodes[i].ShowId).Distinct().Count() < 2)
                {
                    continue;
                }
                var parts = group.Select(i => episodes[i])
                    .Select(e => new CrossoverPart(e.ShowId, shows[e.ShowId].Title, shows[e.ShowId].PosterUrl, e.Id, e.SeasonNumber,
                        e.EpisodeNumber, e.Title, e.AirsAtUtc))
                    .ToList();
                if (parts[^1].AirsAtUtc < now || parts[0].AirsAtUtc > now + Horizon)
                {
                    continue;
                }
                var name = labels[group[0]]!;
                found.Add(new Crossover(IdOf(hub.Id, name, parts[0].AirsAtUtc.Year), name, hub.Id, universeName, parts));
            }
        }
        return found.OrderBy(c => c.StartsAtUtc).ToList();
    }

    /// <summary>
    /// For episodes in air order, each one's crossover event name ("Infection"), or null when no other show of the set
    /// airs an episode of the same name within <see cref="Window"/>.
    /// </summary>
    internal static string?[] Label(IReadOnlyList<(Guid ShowId, string Title, DateTimeOffset AirsAtUtc)> episodes)
    {
        var labels = new string?[episodes.Count];
        for (var i = 0; i < episodes.Count; i++)
        {
            if (Key(episodes[i].Title) is not { } key)
            {
                continue;
            }
            for (var j = i + 1; j < episodes.Count && episodes[j].AirsAtUtc - episodes[i].AirsAtUtc <= Window; j++)
            {
                if (episodes[j].ShowId != episodes[i].ShowId && string.Equals(Key(episodes[j].Title), key, StringComparison.OrdinalIgnoreCase))
                {
                    labels[i] ??= TrimPart(episodes[i].Title);
                    labels[j] = labels[i];
                }
            }
        }
        return labels;
    }

    /// <summary>The labelled episodes grouped into events: the same name, each part within the window of the one before.</summary>
    private static IEnumerable<List<int>> Groups(string?[] labels, List<DateTimeOffset> airs)
    {
        var open = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] is not { } label)
            {
                continue;
            }
            if (open.TryGetValue(label, out var group) && airs[i] - airs[group[^1]] <= Window)
            {
                group.Add(i);
                continue;
            }
            if (group is not null)
            {
                yield return group;
            }
            open[label] = [i];
        }
        foreach (var group in open.Values)
        {
            yield return group;
        }
    }

    /// <summary>A stable id from the universe, the event's name and its year (first 16 bytes of a SHA-256, version 8).</summary>
    private static Guid IdOf(Guid universe, string name, int year)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"crossover:{universe}:{Key(name) ?? name}:{year}"))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The title without its part marker: "Infection" for "Infection, Part II" or "Sister City (Part I)".</summary>
    private static string TrimPart(string title) => PartMarker().Replace(title, "").Trim();

    /// <summary>
    /// What two shows' episodes must share to be one crossover event: the title without its part marker, normalised.
    /// Null for placeholder titles ("TBA", "Episode 5"), which every show uses.
    /// </summary>
    private static string? Key(string title)
    {
        var name = TrimPart(title);
        return name.Length < 3 || Placeholders.Contains(name) || name.StartsWith("Episode ", StringComparison.OrdinalIgnoreCase)
            ? null
            : string.Join(' ', Words().Matches(name.ToLowerInvariant()).Select(m => m.Value));
    }

    [GeneratedRegex(@"[\s,:;-]*(\(\s*(part\s*)?(\d+|i{1,3}|iv)\s*\)|\bpart\s*(\d+|one|two|three|four|i{1,3}|iv)\b)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PartMarker();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Words();
}

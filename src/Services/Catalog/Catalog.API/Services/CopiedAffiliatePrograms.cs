using System.Text.Json;
using Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Services;

/// <summary>
/// The affiliate programs in force, from Catalog's copy of what Financials.API published
/// (<see cref="AffiliateProgramsCopy"/>): the order they're tried in is theirs. Cached briefly, as each click reads
/// them; an admin's change shows within <see cref="CacheFor"/> of arriving. With no copy yet (a new deployment, before
/// Financials has said anything) there are none, and links go out plain.
/// </summary>
public sealed class CopiedAffiliatePrograms(CatalogContext db, IMemoryCache cache)
{
    private const string CacheKey = nameof(CopiedAffiliatePrograms);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<AffiliateProgram>> AllAsync(CancellationToken ct)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<AffiliateProgram>? programs))
        {
            var copy = await db.AffiliatePrograms.AsNoTracking().Select(c => c.Programs).SingleOrDefaultAsync(ct);
            programs = Read(copy);
            cache.Set(CacheKey, programs, CacheFor);
        }
        return programs!;
    }

    internal static IReadOnlyList<AffiliateProgram> Read(string? copy) =>
        copy is null ? [] : (JsonSerializer.Deserialize<List<AffiliateProgramLink>>(copy, JsonSerializerOptions.Web) ?? []).Select(p => p.ToProgram()).ToList();

    internal static string Write(IReadOnlyList<AffiliateProgramLink> programs) => JsonSerializer.Serialize(programs, JsonSerializerOptions.Web);
}

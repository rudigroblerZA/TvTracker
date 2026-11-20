using Catalog.Worker.Providers;

namespace Catalog.IntegrationTests;

/// <summary>
/// A catalog provider that says what a test tells it to: the shows it has, by its own ids. The fifth adapter at the
/// provider seam, beside TVmaze, Wikidata, TheTVDB and the mock.
/// </summary>
public sealed class ScriptedCatalogProvider : ICatalogProvider
{
    public Dictionary<string, ProviderShow> Shows { get; } = [];

    public string Name => "scripted";

    public Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Shows.GetValueOrDefault(externalId));

    public Task<IReadOnlyList<string>> GetFeaturedShowIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Shows.Keys]);

    public Task<IReadOnlyList<string>> GetPopularShowIdsAsync(int count, Action<int>? onPage = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Shows.Keys.Take(count)]);

    public Task<IReadOnlyList<string>> SearchShowIdsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Shows.Where(s => s.Value.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(s => s.Key).Take(limit)]);

    public Task<IReadOnlyList<ProviderShowSummary>> SearchShowsAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderShowSummary>>([]);

    public Task<string?> LookUpShowByTvdbAsync(string tvdbId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> LookUpShowByImdbAsync(string imdbId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetChangedShowsAsync(TimeSpan within, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(null);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetChangedPeopleAsync(TimeSpan within, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(null);

    public Task<ProviderCareer?> GetCareerAsync(string personExternalId, CancellationToken cancellationToken = default) => Task.FromResult<ProviderCareer?>(null);

    public Task<IReadOnlyList<ProviderActingRole>?> GetCastCreditsAsync(string personExternalId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderActingRole>?>(null);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Catalog.ImportCli;

/// <summary>
/// Reading the catalog's public endpoints for the eval commands.
/// </summary>
internal static class EvalHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <param name="token">A member's access token, for what their plan decides; none asks signed out.</param>
    public static HttpClient Client(string catalogUrl, string? token = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(catalogUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
        return http;
    }

    /// <summary>
    /// The answer at <paramref name="path"/>, or null when there is nothing there. The endpoints are rate limited as
    /// search is; a 429 says how long to wait, and this waits and asks again.
    /// </summary>
    public static async Task<T?> GetAsync<T>(HttpClient http, string path, CancellationToken ct)
    {
        while (true)
        {
            using var response = await http.GetAsync(path, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
    }

    /// <summary>
    /// As <see cref="GetAsync{T}"/>, for a path that is always there on a catalog: nothing at it means the address is wrong.
    /// </summary>
    public static async Task<T> GetRequiredAsync<T>(HttpClient http, string path, CancellationToken ct) =>
        await GetAsync<T>(http, path, ct) ?? throw new CliException($"No catalog at {http.BaseAddress}: is the address right?");

    /// <summary>
    /// A command's cases, from its file.
    /// </summary>
    public static async Task<T[]> ReadAsync<T>(string file, CancellationToken ct) =>
        JsonSerializer.Deserialize<T[]>(await File.ReadAllTextAsync(file, ct), Json) ?? [];

    /// <summary>
    /// An earlier run's saved results by their key, to compare this run with; empty when no file was given.
    /// </summary>
    public static async Task<Dictionary<string, T>> BaselineAsync<T>(string? file, Func<T, string> key, CancellationToken ct) =>
        file is null ? [] : (await ReadAsync<T>(file, ct)).ToDictionary(key);

    /// <summary>
    /// Saves a run's results for a later run to compare with, when a file was asked for.
    /// </summary>
    public static async Task SaveAsync<T>(string? file, IReadOnlyCollection<T> results, CancellationToken ct)
    {
        if (file is not null)
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(results, Json), ct);
        }
    }

    /// <summary>
    /// The catalog's show a case names: by title, and by the year it started when the case gives one. Either the show's
    /// id, or why it couldn't be told (not in the catalog, or more than one show it could be).
    /// </summary>
    public static async Task<(Guid? Id, string? Why)> ResolveShowAsync(HttpClient http, string title, int? year, CancellationToken ct)
    {
        var suggestions = await GetRequiredAsync<List<Suggestion>>(http, $"api/v1/shows/suggest?q={Uri.EscapeDataString(title)}", ct);
        var titled = suggestions.Where(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase)).ToList();
        return Expected(titled.Select(s => (s.Id, s.Title, s.Year)), title, year) is { } id
            ? (id, null)
            : (null, WhySkipped(title, year, titled.Select(s => s.Year).ToList()));
    }

    /// <summary>
    /// The one show among <paramref name="suggestions"/> that a case means: titled so, and from that year when the case
    /// gives one. Null when there is none, or more than one (the case then needs a year).
    /// </summary>
    public static Guid? Expected(IEnumerable<(Guid Id, string Title, int? Year)> suggestions, string title, int? year)
    {
        var matches = suggestions
            .Where(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase) && (year is null || s.Year == year))
            .Select(s => s.Id)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Why a case's show couldn't be told: what the catalog has under that title against what the case asked for.
    /// </summary>
    /// <param name="years">The start year of each show in the catalog with that title.</param>
    public static string WhySkipped(string title, int? year, IReadOnlyList<int?> years)
    {
        var found = string.Join(", ", years.Select(y => y?.ToString() ?? "no year"));
        return (years.Count, year) switch
        {
            (0, _) => $"{title} isn't in the catalog",
            (_, { } wanted) when !years.Contains(wanted) => $"no show titled {title} started in {wanted} (the catalog has: {found})",
            (_, { } wanted) => $"{years.Count(y => y == wanted)} shows titled {title} started in {wanted}",
            _ => $"{years.Count} shows are titled {title} ({found}): give the case a year"
        };
    }

    private sealed record Suggestion(Guid Id, string Title, int? Year);
}

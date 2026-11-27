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
}

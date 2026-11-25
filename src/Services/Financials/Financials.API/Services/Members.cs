using System.Net.Http.Headers;

namespace Financials.API.Services;

/// <param name="NewAccounts">When each account made in the window was made.</param>
public sealed record AccountsSummary(int Accounts, int OnPaidPlan, IReadOnlyList<DateTime> NewAccounts);

/// <summary>
/// What Financials asks Identity.API, which owns the members: the caller's region (prices are by region) and, for the
/// revenue report, how many accounts there are. Each call forwards the caller's own token, so Identity answers for
/// that person, or checks that they're an admin, itself.
/// </summary>
public sealed class Members(IHttpClientFactory clients, ILogger<Members> logger)
{
    public const string Client = "identity";

    private sealed record Profile(string? Region);

    /// <summary>
    /// The caller's profile region. Asking makes their profile if they have none yet, so a first payment has someone
    /// to credit. False when Identity didn't answer.
    /// </summary>
    public async Task<(bool Known, string? Region)> RegionAsync(string? authorization, CancellationToken ct)
    {
        var profile = await GetAsync<Profile>("api/v1/profile", authorization, ct);
        return (profile is not null, profile?.Region is { Length: > 0 } region ? region : null);
    }

    /// <summary>
    /// The accounts, and those made in the last <paramref name="days"/> days; null when Identity didn't answer.
    /// </summary>
    public Task<AccountsSummary?> AccountsAsync(string? authorization, int days, CancellationToken ct) =>
        GetAsync<AccountsSummary>($"api/v1/admin/accounts?days={days}", authorization, ct);

    private async Task<T?> GetAsync<T>(string url, string? authorization, CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (AuthenticationHeaderValue.TryParse(authorization, out var header))
            {
                request.Headers.Authorization = header;
            }
            using var response = await clients.CreateClient(Client).SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't ask Identity for {Url}", url);
            return null;
        }
    }
}

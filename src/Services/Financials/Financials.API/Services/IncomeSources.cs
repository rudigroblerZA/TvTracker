using System.Net;
using System.Net.Http.Headers;

namespace Financials.API.Services;

/// <summary>
/// Where the money comes in, asked of the services that hold it: subscription payments from Identity.API and
/// affiliate commission from Catalog.API. This service keeps no copy of either.
/// <list type="bullet">
/// <item><b>As the admin who asked:</b> their own token is passed on, so each service applies its own admin check and
/// this one gains no rights of its own over their data.</item>
/// <item><b>A service that can't be reached is a gap, not a failure:</b> null comes back and the statement says which
/// half is missing, so the costs can still be read and changed.</item>
/// </list>
/// </summary>
public sealed class IncomeSources(IHttpClientFactory clients, ILogger<IncomeSources> logger)
{
    public const string Identity = "identity-income";
    public const string Catalog = "catalog-income";

    /// <summary>
    /// Null when Identity.API couldn't be asked.
    /// </summary>
    public async Task<SubscriptionIncome?> SubscriptionsAsync(string? authorization, int months, CancellationToken ct)
    {
        var (status, income) = await GetAsync<SubscriptionIncome>(Identity, $"api/v1/admin/income?months={months}", authorization, ct);
        return status == HttpStatusCode.OK ? income : null;
    }

    /// <summary>
    /// The commission, and whether affiliate links are on at all. Catalog.API answers with no content while its flag
    /// is off. Anything else that isn't the months, a 404 included, is a gap: the address may be wrong or Catalog an
    /// older one, and calling that "off" would hide commission that exists.
    /// </summary>
    public async Task<AffiliateIncome> CommissionAsync(string? authorization, int months, CancellationToken ct)
    {
        var (status, commission) = await GetAsync<List<CommissionMonth>>(Catalog, $"api/v1/affiliates/monthly?months={months}", authorization, ct);
        return status == HttpStatusCode.NoContent ? AffiliateIncome.Off : commission is null ? AffiliateIncome.Unknown : AffiliateIncome.On(commission);
    }

    private async Task<(HttpStatusCode? Status, T? Body)> GetAsync<T>(string client, string url, string? authorization, CancellationToken ct)
        where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (AuthenticationHeaderValue.TryParse(authorization, out var header))
            {
                request.Headers.Authorization = header;
            }
            using var response = await clients.CreateClient(client).SendAsync(request, ct);
            return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<T>(ct) : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't ask {Service} for income; the statement goes without it", client);
            return (null, null);
        }
    }
}

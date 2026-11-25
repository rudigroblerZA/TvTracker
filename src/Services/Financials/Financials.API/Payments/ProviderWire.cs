using System.Text.Json;

namespace Financials.API.Payments;

/// <summary>
/// What the providers' classes share: the metadata key that ties a provider's subscription to the app's checkout,
/// amounts in a currency's smallest unit, and the small pieces of reading a webhook and calling an API.
/// </summary>
internal static class ProviderWire
{
    /// <summary>
    /// The metadata key that carries the app's checkout session id at a provider.
    /// </summary>
    public const string SessionKey = "checkout_session";

    /// <summary>
    /// Currencies counted in whole units, not hundredths (Stripe's "zero-decimal" list, which Polar, built on Stripe,
    /// shares). HUF, TWD and ISK have rules of their own at Stripe (whole units in some places, hundredths in others);
    /// price them in whole amounts.
    /// </summary>
    private static readonly HashSet<string> ZeroDecimal =
        ["bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"];

    /// <summary>
    /// Currencies counted in thousandths, whose amounts must end in 0 (the "three-decimal" list).
    /// </summary>
    private static readonly HashSet<string> ThreeDecimal = ["bhd", "jod", "kwd", "omr", "tnd"];

    private static decimal Scale(string currency) =>
        ZeroDecimal.Contains(currency.ToLowerInvariant()) ? 1m : ThreeDecimal.Contains(currency.ToLowerInvariant()) ? 1000m : 100m;

    public static long MinorUnits(decimal amount, string currency)
    {
        var units = (long)Math.Round(amount * Scale(currency), MidpointRounding.AwayFromZero);
        return ThreeDecimal.Contains(currency.ToLowerInvariant()) ? (long)Math.Round(units / 10m, MidpointRounding.AwayFromZero) * 10 : units;
    }

    public static decimal FromMinorUnits(long amount, string currency) => amount / Scale(currency);

    /// <summary>
    /// A signed event the app doesn't act on: acknowledged, not recorded.
    /// </summary>
    public static PaymentEvent Ignored(string id) => new(id, PaymentEventKind.Ignored, "");

    /// <summary>
    /// A property's text, or null when the element isn't an object, has no such property, or it isn't a string.
    /// </summary>
    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string AddQuery(Uri uri, string query) =>
        new UriBuilder(uri) { Query = string.IsNullOrEmpty(uri.Query) ? query : $"{uri.Query.TrimStart('?')}&{query}" }.Uri.ToString();

    /// <summary>
    /// Sends <paramref name="request"/> with the provider's key as a bearer token; anything but success is an
    /// <see cref="HttpRequestException"/> that says what the provider answered.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string? key, string provider,
        string keySetting, CancellationToken ct)
    {
        request.Headers.Authorization = new("Bearer", key ?? throw new InvalidOperationException($"{keySetting} isn't set."));
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new HttpRequestException($"{provider} answered {(int)response.StatusCode} to {request.Method} {request.RequestUri}: {error}",
                null, response.StatusCode);
        }
        return response;
    }
}

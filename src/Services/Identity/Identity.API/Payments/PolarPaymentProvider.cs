using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.API.Data;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;
using TvTracker.Payments;

namespace Identity.API.Payments;

/// <summary>
/// Polar (polar.sh), a merchant of record: it is the seller, so it charges and pays the sales tax and VAT wherever the
/// buyer is, and pays out to countries Stripe doesn't open accounts in (South Africa among them). Over its REST API
/// with a plain HttpClient, like <see cref="StripePaymentProvider"/>: three calls and four webhook events.
/// <list type="bullet">
/// <item><b>Products are made at Polar first:</b> one recurring product for each plan and interval
/// (<see cref="PolarOptions.Products"/>), since at Polar the product, not the price, says how often it renews. The
/// amount still comes from <c>Payments:Prices</c>: the checkout overrides the product's price with the session's own
/// (<c>prices</c>, an ad-hoc fixed price), so there is one price list.</item>
/// <item><b>Checkout:</b> a checkout session for that product, with the app's session id in its metadata, which Polar
/// copies to the order and the subscription, and the user's id as the customer's external id.</item>
/// <item><b>Webhooks</b> (Standard Webhooks: <c>webhook-id</c>, <c>webhook-timestamp</c>, <c>webhook-signature</c>):
/// <c>order.paid</c> for a subscription is a payment (the first one names the checkout),
/// <c>subscription.canceled</c> with its period still running a cancellation scheduled,
/// <c>subscription.uncanceled</c> one taken back, <c>subscription.revoked</c> the end. Anything else signed is
/// acknowledged and ignored. The delivery's <c>webhook-id</c> is the event's id: Polar's payload has none of its own,
/// and that header stays the same when a delivery is retried.</item>
/// <item><b>Managing billing</b> is Polar's customer portal (<see cref="PolarOptions.PortalUrl"/>), where the buyer
/// signs in with their email.</item>
/// </list>
/// </summary>
public sealed class PolarPaymentProvider(HttpClient http, IOptions<PaymentOptions> options) : IPaymentProvider
{
    public const string ProviderName = "polar";

    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    /// <summary>
    /// The metadata key that carries the app's checkout session id, the same as Stripe's.
    /// </summary>
    public const string SessionKey = StripePaymentProvider.SessionKey;

    private PolarOptions Polar => options.Value.Polar;

    public string Name => ProviderName;

    public async Task<Uri> CreateCheckoutAsync(CheckoutSession session, string returnUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var back))
        {
            throw new InvalidOperationException("Payments:ReturnUrl must be an absolute address for Polar's checkout to come back to.");
        }
        var product = Polar.Products.For(session.Plan, session.Interval)
            ?? throw new InvalidOperationException($"Payments:Polar:Products has no product for {session.Plan} by the {session.Interval}.");
        using var response = await SendAsync(HttpMethod.Post, "v1/checkouts/", CheckoutBody(session, product, back), ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new Uri(json.RootElement.GetProperty("url").GetString()!);
    }

    /// <summary>
    /// What a checkout is made with: the product for the plan and interval, at the session's own amount.
    /// </summary>
    internal static Dictionary<string, object> CheckoutBody(CheckoutSession session, string product, Uri back) => new()
    {
        ["products"] = new[] { product },
        ["prices"] = new Dictionary<string, object>
        {
            [product] = new[]
            {
                new Dictionary<string, object>
                {
                    ["amount_type"] = "fixed",
                    ["price_amount"] = StripePaymentProvider.MinorUnits(session.Amount, session.Currency),
                    ["price_currency"] = session.Currency.ToLowerInvariant()
                }
            }
        },
        ["success_url"] = AddQuery(back, "checkout=done"),
        ["return_url"] = back.ToString(),
        // Polar keeps one customer for each of the app's users, whatever email they pay with.
        ["external_customer_id"] = session.UserId,
        ["metadata"] = new Dictionary<string, string> { [SessionKey] = session.Id.ToString(), ["plan"] = session.Plan }
    };

    public async Task CancelAsync(Subscription subscription, bool now, CancellationToken ct)
    {
        var path = $"v1/subscriptions/{Uri.EscapeDataString(subscription.ProviderSubscriptionId)}";
        using var _ = now
            ? await SendAsync(HttpMethod.Delete, path, null, ct)
            : await SendAsync(HttpMethod.Patch, path, new Dictionary<string, object> { ["cancel_at_period_end"] = true }, ct);
    }

    public async Task ResumeAsync(Subscription subscription, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Patch, $"v1/subscriptions/{Uri.EscapeDataString(subscription.ProviderSubscriptionId)}",
            new Dictionary<string, object> { ["cancel_at_period_end"] = false }, ct);
    }

    public string? ManageUrl(Subscription subscription) => Polar.PortalUrl is { Length: > 0 } url ? url : null;

    public PaymentEvent? ReadWebhook(string body, IHeaderDictionary headers, DateTimeOffset now)
    {
        var id = headers[IdHeader].ToString();
        return Polar.WebhookSecret is { Length: > 0 } secret
            && Verify(body, id, headers[TimestampHeader].ToString(), headers[SignatureHeader].ToString(), secret, now)
            ? Read(id, body)
            : null;
    }

    /// <summary>
    /// Standard Webhooks' check: one of the header's <c>v1,{base64}</c> signatures is the HMAC-SHA256 of
    /// <c>{id}.{timestamp}.{body}</c>, and the timestamp is within <see cref="PaymentSignature.Tolerance"/> of now.
    /// <para>The key is tried both ways Polar has made secrets: the bytes that follow <c>whsec_</c>, base64-decoded (the
    /// standard, for secrets made since September 2026), and the secret's own text as bytes (Polar's earlier ones).</para>
    /// </summary>
    internal static bool Verify(string body, string id, string timestamp, string signatures, string secret, DateTimeOffset now)
    {
        if (id.Length == 0 || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > PaymentSignature.Tolerance)
        {
            return false;
        }
        var given = signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.StartsWith("v1,", StringComparison.Ordinal))
            .Select(s => s[3..])
            .ToList();
        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}");
        return Keys(secret).Any(key =>
        {
            var expected = Encoding.ASCII.GetBytes(Convert.ToBase64String(HMACSHA256.HashData(key, signed)));
            return given.Any(g => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(g)));
        });
    }

    internal static string Sign(string body, string id, DateTimeOffset at, byte[] key)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{t}.{body}")));
    }

    private static IEnumerable<byte[]> Keys(string secret)
    {
        const string prefix = "whsec_";
        if (secret.StartsWith(prefix, StringComparison.Ordinal))
        {
            var decoded = new byte[secret.Length];
            if (Convert.TryFromBase64String(secret[prefix.Length..], decoded, out var written) && written > 0)
            {
                yield return decoded[..written];
            }
        }
        yield return Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>
    /// A signed event's payload, read into what the app acts on: <see cref="PaymentEventKind.Ignored"/> for an event it
    /// doesn't (Polar must still get a 2xx, or it retries and then switches the endpoint off), null for one it can't read.
    /// </summary>
    internal static PaymentEvent? Read(string id, string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var data = root.GetProperty("data");
            return root.GetProperty("type").GetString() switch
            {
                "order.paid" => Paid(id, data),
                // Cancelled at the period's end, which is still to come. One ended on the spot is also "canceled", and
                // is followed by "revoked", which is what ends it here.
                "subscription.canceled" => data.TryGetProperty("cancel_at_period_end", out var atEnd) && atEnd.ValueKind == JsonValueKind.True
                        && Text(data, "status") is "active" or "trialing"
                    ? new PaymentEvent(id, PaymentEventKind.CancelScheduled, data.GetProperty("id").GetString()!)
                    : Ignored(id),
                "subscription.uncanceled" => new PaymentEvent(id, PaymentEventKind.Resumed, data.GetProperty("id").GetString()!),
                "subscription.revoked" => new PaymentEvent(id, PaymentEventKind.Ended, data.GetProperty("id").GetString()!),
                _ => Ignored(id)
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException
            or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static PaymentEvent Ignored(string id) => new(id, PaymentEventKind.Ignored, "");

    /// <summary>
    /// A paid order. One that isn't for a subscription (none are sold here) is ignored.
    /// </summary>
    private static PaymentEvent Paid(string id, JsonElement order)
    {
        if (Text(order, "subscription_id") is not { } subscription)
        {
            return Ignored(id);
        }
        var of = order.TryGetProperty("subscription", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        // The checkout's metadata is copied to the order and to the subscription; a renewal's order has the subscription's.
        var session = Guid.TryParse(Metadata(order) ?? Metadata(of), out var parsed) ? parsed : (Guid?)null;
        var currency = Text(order, "currency");
        // What the buyer paid, tax included, in the currency's smallest unit.
        decimal? amount = order.TryGetProperty("total_amount", out var total) && total.ValueKind == JsonValueKind.Number && currency is not null
            ? StripePaymentProvider.FromMinorUnits(total.GetInt64(), currency)
            : null;
        DateTime? periodEnd = Text(of, "current_period_end") is { } end
            && DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime
            : null;
        return new PaymentEvent(id, PaymentEventKind.Paid, subscription, session, Text(order, "id"), amount, currency?.ToUpperInvariant(), periodEnd);
    }

    private static string? Metadata(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("metadata", out var metadata) ? Text(metadata, SessionKey) : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string AddQuery(Uri uri, string query) =>
        new UriBuilder(uri) { Query = string.IsNullOrEmpty(uri.Query) ? query : $"{uri.Query.TrimStart('?')}&{query}" }.Uri.ToString();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", Polar.AccessToken
            ?? throw new InvalidOperationException("Payments:Polar:AccessToken isn't set."));
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new HttpRequestException($"Polar answered {(int)response.StatusCode} to {method} {path}: {error}", null, response.StatusCode);
        }
        return response;
    }
}

/// <summary>
/// Polar's settings (<c>Payments:Polar:*</c>): the token and the webhook secret from secrets, the rest plain.
/// </summary>
public sealed class PolarOptions
{
    /// <summary>
    /// An organization access token (<c>polar_oat_…</c>) that may write checkouts and subscriptions. The sandbox has
    /// tokens of its own: one from production doesn't work there.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The webhook endpoint's signing secret.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// The customer portal's address, shown as "manage billing".
    /// </summary>
    public string? PortalUrl { get; set; }

    /// <summary>
    /// The API's address: <c>https://sandbox-api.polar.sh/</c> to test against Polar's sandbox.
    /// </summary>
    public string ApiBase { get; set; } = "https://api.polar.sh/";

    public PolarProducts Products { get; set; } = new();
}

/// <summary>
/// The ids of the recurring products made at Polar, one for each plan and interval: the product is what says "monthly"
/// or "yearly" there, and carries the name the buyer sees. Their prices at Polar don't matter: every checkout sets its own.
/// </summary>
public sealed class PolarProducts
{
    public string? PlusMonth { get; set; }
    public string? PlusYear { get; set; }
    public string? FamilyMonth { get; set; }
    public string? FamilyYear { get; set; }

    public string? For(string plan, string interval) => (plan, interval) switch
    {
        (Plans.Plus, PlanPrices.Month) => Blank(PlusMonth),
        (Plans.Plus, PlanPrices.Year) => Blank(PlusYear),
        (Plans.Family, PlanPrices.Month) => Blank(FamilyMonth),
        (Plans.Family, PlanPrices.Year) => Blank(FamilyYear),
        _ => null
    };

    private static string? Blank(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
}

using System.Globalization;
using System.Text.Json;
using Identity.API.Data;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;
using TvTracker.Payments;

namespace Identity.API.Payments;

/// <summary>
/// Stripe, over its REST API with a plain HttpClient:
/// the app needs four calls and three webhook events, which isn't worth an SDK and its release train.
/// <list type="bullet">
/// <item><b>Checkout:</b> a Checkout Session in subscription mode for the <see cref="CheckoutSession"/>'s own amount and
/// interval (inline <c>price_data</c>, so <c>Payments:Prices</c> stays the one price list), with the session's id in the
/// subscription's metadata, which every invoice of it carries back; the session id is also the idempotency key, so a
/// retried request makes one checkout.</item>
/// <item><b>Webhooks</b> (signed like the test provider's, <see cref="PaymentSignature"/>, header <c>Stripe-Signature</c>):
/// <c>invoice.paid</c> is a payment (the first one names the checkout), <c>customer.subscription.updated</c> a cancel
/// scheduled or taken back when that's what changed, <c>customer.subscription.deleted</c> the end. Anything else
/// signed is acknowledged and ignored.</item>
/// <item><b>Both payload shapes:</b> from API version 2025-03-31 an invoice's subscription and its metadata sit under
/// <c>parent.subscription_details</c>; before, at the top. The webhook endpoint's version decides, so both are read.</item>
/// <item><b>Managing billing</b> (card, invoices) is Stripe's customer portal through its login link
/// (<see cref="StripeOptions.PortalUrl"/>): the app keeps no Stripe customer id to make a portal session with.</item>
/// </list>
/// </summary>
public sealed class StripePaymentProvider(HttpClient http, IOptions<PaymentOptions> options) : IPaymentProvider
{
    public const string ProviderName = "stripe";

    public const string SignatureHeader = "Stripe-Signature";

    /// <summary>
    /// The subscription metadata key that carries the app's checkout session id.
    /// </summary>
    public const string SessionKey = "checkout_session";

    /// <summary>
    /// Currencies Stripe counts in whole units, not hundredths (its "zero-decimal" list). HUF, TWD and ISK have rules of
    /// their own at Stripe (whole units in some places, hundredths in others); price them in whole amounts.
    /// </summary>
    private static readonly HashSet<string> ZeroDecimal =
        ["bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"];

    private StripeOptions Stripe => options.Value.Stripe;

    public string Name => ProviderName;

    public async Task<Uri> CreateCheckoutAsync(CheckoutSession session, string returnUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var back))
        {
            throw new InvalidOperationException("Payments:ReturnUrl must be an absolute address for Stripe's checkout to come back to.");
        }
        var form = new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["client_reference_id"] = session.Id.ToString(),
            ["success_url"] = AddQuery(back, "checkout=done"),
            ["cancel_url"] = back.ToString(),
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = session.Currency.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = MinorUnits(session.Amount, session.Currency).ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][recurring][interval]"] = session.Interval,
            ["line_items[0][price_data][product_data][name]"] = Plans.Name(session.Plan),
            ["metadata[" + SessionKey + "]"] = session.Id.ToString(),
            ["subscription_data[metadata][" + SessionKey + "]"] = session.Id.ToString(),
            ["subscription_data[metadata][plan]"] = session.Plan
        };
        using var response = await SendAsync(HttpMethod.Post, "v1/checkout/sessions", form, idempotencyKey: $"checkout-{session.Id}", ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new Uri(json.RootElement.GetProperty("url").GetString()!);
    }

    public async Task CancelAsync(Subscription subscription, bool now, CancellationToken ct)
    {
        var path = $"v1/subscriptions/{Uri.EscapeDataString(subscription.ProviderSubscriptionId)}";
        using var _ = now
            ? await SendAsync(HttpMethod.Delete, path, null, null, ct)
            : await SendAsync(HttpMethod.Post, path, new() { ["cancel_at_period_end"] = "true" }, null, ct);
    }

    public async Task ResumeAsync(Subscription subscription, CancellationToken ct)
    {
        // Both ways a cancellation is scheduled are cleared: the app's own (cancel_at_period_end) and the customer portal's
        // on newer API versions (cancel_at, unset by sending it empty).
        using var _ = await SendAsync(HttpMethod.Post, $"v1/subscriptions/{Uri.EscapeDataString(subscription.ProviderSubscriptionId)}",
            new() { ["cancel_at_period_end"] = "false", ["cancel_at"] = "" }, null, ct);
    }

    public string? ManageUrl(Subscription subscription) => Stripe.PortalUrl is { Length: > 0 } url ? url : null;

    public PaymentEvent? ReadWebhook(string body, IHeaderDictionary headers, DateTimeOffset now) =>
        Stripe.WebhookSecret is { Length: > 0 } secret && PaymentSignature.Verify(body, headers[SignatureHeader].ToString(), secret, now)
            ? Read(body)
            : null;

    /// <summary>
    /// A signed event's payload, read into what the app acts on: <see cref="PaymentEventKind.Ignored"/> for an event it
    /// doesn't (Stripe must still get a 2xx, or it retries for days), null for one it can't read.
    /// </summary>
    internal static PaymentEvent? Read(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var data = root.GetProperty("data");
            var obj = data.GetProperty("object");
            return root.GetProperty("type").GetString() switch
            {
                "invoice.paid" => Paid(id, obj),
                "customer.subscription.updated" => Updated(id, obj, data.TryGetProperty("previous_attributes", out var before) ? before : default),
                "customer.subscription.deleted" => new PaymentEvent(id, PaymentEventKind.Ended, obj.GetProperty("id").GetString()!),
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
    /// A paid invoice. One not for a subscription (none are made here) is ignored.
    /// </summary>
    private static PaymentEvent Paid(string id, JsonElement invoice)
    {
        // API 2025-03-31 and later: parent.subscription_details; before: subscription and subscription_details.
        var details = invoice.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty("subscription_details", out var d) && d.ValueKind == JsonValueKind.Object
            ? d
            : invoice.TryGetProperty("subscription_details", out var old) ? old : default;
        var subscription = Text(details, "subscription") ?? Text(invoice, "subscription");
        if (subscription is null)
        {
            return Ignored(id);
        }
        var session = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("metadata", out var metadata)
            && Guid.TryParse(Text(metadata, SessionKey), out var s) ? s : (Guid?)null;
        var currency = Text(invoice, "currency");
        decimal? amount = invoice.TryGetProperty("amount_paid", out var paid) && currency is not null
            ? FromMinorUnits(paid.GetInt64(), currency)
            : null;
        // The latest end among the lines: a plan change's invoice also has proration lines for time already passed.
        var ends = invoice.TryGetProperty("lines", out var lines)
            ? lines.GetProperty("data").EnumerateArray()
                .Where(l => l.TryGetProperty("period", out var p) && p.TryGetProperty("end", out _))
                .Select(l => l.GetProperty("period").GetProperty("end").GetInt64())
                .ToList()
            : [];
        DateTime? periodEnd = ends.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(ends.Max()).UtcDateTime : null;
        return new PaymentEvent(id, PaymentEventKind.Paid, subscription, session, Text(invoice, "id"), amount, currency?.ToUpperInvariant(), periodEnd);
    }

    /// <summary>
    /// A cancellation scheduled or taken back, when that's what changed: <c>cancel_at_period_end</c>, or <c>cancel_at</c>,
    /// which the customer portal sets on newer API versions. Any other change (a card, a renewal's period) is ignored.
    /// </summary>
    private static PaymentEvent Updated(string id, JsonElement subscription, JsonElement before)
    {
        if (before.ValueKind != JsonValueKind.Object
            || !(before.TryGetProperty("cancel_at_period_end", out _) || before.TryGetProperty("cancel_at", out _)))
        {
            return Ignored(id);
        }
        var cancelling = subscription.TryGetProperty("cancel_at_period_end", out var atEnd) && atEnd.GetBoolean()
            || subscription.TryGetProperty("cancel_at", out var at) && at.ValueKind == JsonValueKind.Number;
        return new PaymentEvent(id, cancelling ? PaymentEventKind.CancelScheduled : PaymentEventKind.Resumed, subscription.GetProperty("id").GetString()!);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Currencies Stripe counts in thousandths, whose amounts must end in 0 (its "three-decimal" list).
    /// </summary>
    private static readonly HashSet<string> ThreeDecimal = ["bhd", "jod", "kwd", "omr", "tnd"];

    private static decimal Scale(string currency) =>
        ZeroDecimal.Contains(currency.ToLowerInvariant()) ? 1m : ThreeDecimal.Contains(currency.ToLowerInvariant()) ? 1000m : 100m;

    internal static long MinorUnits(decimal amount, string currency)
    {
        var units = (long)Math.Round(amount * Scale(currency), MidpointRounding.AwayFromZero);
        return ThreeDecimal.Contains(currency.ToLowerInvariant()) ? (long)Math.Round(units / 10m, MidpointRounding.AwayFromZero) * 10 : units;
    }

    internal static decimal FromMinorUnits(long amount, string currency) => amount / Scale(currency);

    private static string AddQuery(Uri uri, string query) =>
        new UriBuilder(uri) { Query = string.IsNullOrEmpty(uri.Query) ? query : $"{uri.Query.TrimStart('?')}&{query}" }.Uri.ToString();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form, string? idempotencyKey,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = form is null ? null : new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new("Bearer", Stripe.SecretKey
            ?? throw new InvalidOperationException("Payments:Stripe:SecretKey isn't set."));
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new HttpRequestException($"Stripe answered {(int)response.StatusCode} to {method} {path}: {error}", null, response.StatusCode);
        }
        return response;
    }
}

/// <summary>
/// Stripe's settings (<c>Payments:Stripe:*</c>), all from secrets.
/// </summary>
public sealed class StripeOptions
{
    /// <summary>
    /// The API's secret key (<c>sk_test_…</c> in test mode, <c>sk_live_…</c> live).
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The webhook endpoint's signing secret (<c>whsec_…</c>).
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// The customer portal's login link (<c>https://billing.stripe.com/p/login/…</c>), shown as "manage billing".
    /// </summary>
    public string? PortalUrl { get; set; }

    /// <summary>
    /// The API's address; another only to test against a stand-in such as Stripe's <c>stripe-mock</c>.
    /// </summary>
    public string ApiBase { get; set; } = "https://api.stripe.com/";
}

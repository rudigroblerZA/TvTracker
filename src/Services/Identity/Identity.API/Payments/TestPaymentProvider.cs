using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.API.Data;
using Microsoft.Extensions.Options;

namespace Identity.API.Payments;

/// <summary>
/// A pretend provider for trying payments locally: the checkout is the web app's <c>/dev/payments</c> page, which pays,
/// renews, cancels or ends a subscription by posting a webhook signed with <see cref="TestProviderOptions.WebhookSecret"/>.
/// Signed like Stripe's: header <c>TvTracker-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}"}</c>,
/// refused more than <see cref="Tolerance"/> from now, so a captured webhook can't be replayed later. It keeps no state
/// of its own: cancelling and resuming are recorded by the app, as a real provider's webhook would confirm them.
/// </summary>
public sealed class TestPaymentProvider(IOptions<PaymentOptions> options) : IPaymentProvider
{
    public const string ProviderName = "test";
    public const string SignatureHeader = "TvTracker-Signature";
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private TestProviderOptions Test => options.Value.Test;

    public string Name => ProviderName;

    public Task<Uri> CreateCheckoutAsync(CheckoutSession session, string returnUrl, CancellationToken ct)
    {
        var page = (Test.CheckoutUrl ?? throw new InvalidOperationException("Payments:Test:CheckoutUrl isn't set.")).TrimEnd('/');
        // The page sends the buyer back to the plan page on its own site, so the return address isn't passed.
        var query = string.Join("&", new[]
        {
            ("session", session.Id.ToString()), ("plan", session.Plan), ("interval", session.Interval),
            ("amount", session.Amount.ToString(CultureInfo.InvariantCulture)), ("currency", session.Currency)
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return Task.FromResult(new Uri($"{page}/dev/payments/checkout?{query}"));
    }

    public Task CancelAsync(Subscription subscription, bool now, CancellationToken ct) => Task.CompletedTask;

    public Task ResumeAsync(Subscription subscription, CancellationToken ct) => Task.CompletedTask;

    public string? ManageUrl(Subscription subscription) => Test.CheckoutUrl is { } page
        ? $"{page.TrimEnd('/')}/dev/payments/subscription?id={Uri.EscapeDataString(subscription.ProviderSubscriptionId)}&interval={subscription.Interval}"
        : null;

    public PaymentEvent? ReadWebhook(string body, IHeaderDictionary headers, DateTimeOffset now)
    {
        if (Test.WebhookSecret is not { Length: > 0 } secret
            || !Verify(body, headers[SignatureHeader].ToString(), secret, now))
        {
            return null;
        }
        var e = JsonSerializer.Deserialize<TestEvent>(body, JsonSerializerOptions.Web);
        if (e?.Id is not { Length: > 0 } || e.Subscription is not { Length: > 0 })
        {
            return null;
        }
        PaymentEventKind? kind = e.Type switch
        {
            "payment.succeeded" => PaymentEventKind.Paid,
            "subscription.cancel_scheduled" => PaymentEventKind.CancelScheduled,
            "subscription.resumed" => PaymentEventKind.Resumed,
            "subscription.ended" => PaymentEventKind.Ended,
            _ => null
        };
        return kind is { } k ? new PaymentEvent(e.Id, k, e.Subscription, e.Session, e.Payment, e.Amount, e.Currency, e.PeriodEndUtc) : null;
    }

    /// <summary>
    /// The signature header for <paramref name="body"/> at <paramref name="at"/> (what the web app's test page sends).
    /// </summary>
    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Hmac(secret, $"{t}.{body}")}";
    }

    internal static bool Verify(string body, string header, string secret, DateTimeOffset now)
    {
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!parts.TryGetValue("t", out var t) || !parts.TryGetValue("v1", out var v1)
            || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }
        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }
        var expected = Encoding.ASCII.GetBytes(Hmac(secret, $"{t}.{body}"));
        var given = Encoding.ASCII.GetBytes(v1.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static string Hmac(string secret, string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));

    private sealed record TestEvent(string? Id, string? Type, string? Subscription, Guid? Session, string? Payment, decimal? Amount,
        string? Currency, DateTime? PeriodEndUtc);
}

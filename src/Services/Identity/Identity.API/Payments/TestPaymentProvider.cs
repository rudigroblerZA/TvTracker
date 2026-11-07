using System.Globalization;
using System.Text.Json;
using Identity.API.Data;
using Microsoft.Extensions.Options;
using TvTracker.Payments;

namespace Identity.API.Payments;

/// <summary>
/// A pretend provider for trying payments locally: the checkout is the web app's <c>/dev/payments</c> page, which pays,
/// renews, cancels or ends a subscription by posting a webhook signed with <see cref="TestProviderOptions.WebhookSecret"/>.
/// Signed like Stripe's (<see cref="PaymentSignature"/>, shared with the web app's pages). Its pages are linked relative
/// to the page the buyer is on, so the browser keeps the origin (http or https) it signed in on. It keeps no state of
/// its own: cancelling and resuming are recorded by the app, as a real provider's webhook would confirm them.
/// </summary>
public sealed class TestPaymentProvider(IOptions<PaymentOptions> options) : IPaymentProvider
{
    public const string ProviderName = "test";

    public string Name => ProviderName;

    public Task<Uri> CreateCheckoutAsync(CheckoutSession session, string returnUrl, CancellationToken ct)
    {
        // The page sends the buyer back to the plan page on its own site, so the return address isn't passed.
        var query = string.Join("&", new[]
        {
            ("session", session.Id.ToString()), ("plan", session.Plan), ("interval", session.Interval),
            ("amount", session.Amount.ToString(CultureInfo.InvariantCulture)), ("currency", session.Currency)
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return Task.FromResult(new Uri($"/dev/payments/checkout?{query}", UriKind.Relative));
    }

    public Task CancelAsync(Subscription subscription, bool now, CancellationToken ct) => Task.CompletedTask;

    public Task ResumeAsync(Subscription subscription, CancellationToken ct) => Task.CompletedTask;

    public string? ManageUrl(Subscription subscription) =>
        $"/dev/payments/subscription?id={Uri.EscapeDataString(subscription.ProviderSubscriptionId)}&interval={subscription.Interval}";

    public PaymentEvent? ReadWebhook(string body, IHeaderDictionary headers, DateTimeOffset now)
    {
        if (options.Value.Test.WebhookSecret is not { Length: > 0 } secret
            || !PaymentSignature.Verify(body, headers[PaymentSignature.Header].ToString(), secret, now))
        {
            return null;
        }
        TestEvent? e;
        try
        {
            e = JsonSerializer.Deserialize<TestEvent>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private sealed record TestEvent(string? Id, string? Type, string? Subscription, Guid? Session, string? Payment, decimal? Amount,
        string? Currency, DateTime? PeriodEndUtc);
}

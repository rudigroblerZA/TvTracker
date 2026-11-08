using Identity.API.Data;

namespace Identity.API.Payments;

public enum PaymentEventKind
{
    /// <summary>
    /// A payment went through: a new subscription (it names the checkout session) or a renewal.
    /// </summary>
    Paid,

    /// <summary>
    /// Cancelled: it won't renew, and ends when the paid period does.
    /// </summary>
    CancelScheduled,

    /// <summary>
    /// A cancellation taken back before the period ended.
    /// </summary>
    Resumed,

    /// <summary>
    /// Over: the period ended without a renewal, or it was stopped at once.
    /// </summary>
    Ended,

    /// <summary>
    /// A genuine event the app doesn't act on (Stripe sends many): acknowledged, so the provider doesn't retry it, and
    /// not recorded.
    /// </summary>
    Ignored
}

/// <summary>
/// A provider's webhook, read into the few things the app acts on.
/// </summary>
/// <param name="Id">The provider's event id, which makes a resent event a no-op.</param>
/// <param name="SessionId">The checkout it completes, for a new subscription's first payment.</param>
public sealed record PaymentEvent(string Id, PaymentEventKind Kind, string SubscriptionId, Guid? SessionId = null, string? PaymentId = null,
    decimal? Amount = null, string? Currency = null, DateTime? PeriodEndUtc = null);

/// <summary>
/// A payment provider: its checkout, its cancel and resume calls, and its signed webhooks. Everything else (sessions,
/// subscriptions, receipts and the plan change) is the app's own, in <see cref="SubscriptionLedger"/>, so adding Stripe
/// or Paddle is one class.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>
    /// Where to send the buyer to pay for <paramref name="session"/>, returning to <paramref name="returnUrl"/>.
    /// </summary>
    Task<Uri> CreateCheckoutAsync(CheckoutSession session, string returnUrl, CancellationToken ct);

    /// <summary>
    /// Stops renewal (the subscription runs to its period's end), or ends it now when <paramref name="now"/> is set.
    /// </summary>
    Task CancelAsync(Subscription subscription, bool now, CancellationToken ct);

    Task ResumeAsync(Subscription subscription, CancellationToken ct);

    /// <summary>
    /// The event in a webhook (<see cref="PaymentEventKind.Ignored"/> for a signed one the app doesn't act on), or null
    /// when its signature doesn't check out or it can't be read.
    /// </summary>
    PaymentEvent? ReadWebhook(string body, IHeaderDictionary headers, DateTimeOffset now);

    /// <summary>
    /// Where someone manages this subscription at the provider, when it has such a page.
    /// </summary>
    string? ManageUrl(Subscription subscription);
}

/// <summary>
/// The registered providers by name, and the one selling now (<see cref="PaymentOptions.Provider"/>).
/// </summary>
public sealed class PaymentProviders(IEnumerable<IPaymentProvider> providers, Microsoft.Extensions.Options.IOptions<PaymentOptions> options)
{
    private readonly Dictionary<string, IPaymentProvider> byName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IPaymentProvider? Selling =>
        options.Value.Provider is { Length: > 0 } name ? byName.GetValueOrDefault(name) : null;

    public IPaymentProvider? Named(string name) => byName.GetValueOrDefault(name);
}

namespace Financials.Data;

// What the payment providers sold and were paid: checkouts, subscriptions, receipts, the provider events already
// applied, and what an admin set for payments.

/// <summary>
/// What an admin has set for payments on the Financials page (<c>PaymentSettingsApi</c>): the provider
/// that sells, and Stripe's keys. One row; a field left null falls back to configuration (<c>Payments:*</c>).
/// </summary>
public class PaymentSettings
{
    public const int OnlyId = 1;

    public const int UpdatedByLength = 128;
    public const int PortalUrlLength = 512;

    public int Id { get; set; }

    /// <summary>
    /// Null follows configuration; "" sells nothing; else the provider's name.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Encrypted (<c>SettingsCipher</c>); null follows configuration.
    /// </summary>
    public string? StripeSecretKey { get; set; }

    /// <summary>
    /// Encrypted; null follows configuration.
    /// </summary>
    public string? StripeWebhookSecret { get; set; }

    public string? StripePortalUrl { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// The admin who last saved them (a username).
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// That admin's id, so their name can be taken off when their account is deleted. Null on a row saved before this
    /// was kept.
    /// </summary>
    public string? UpdatedById { get; set; }
}

/// <summary>
/// A checkout someone started: which plan and price, for whom. The provider's checkout refers back to it, so a payment
/// is credited to the user who started it rather than to anyone a webhook names.
/// </summary>
public class CheckoutSession
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Provider { get; set; }
    public required string Plan { get; set; }
    public required string Interval { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>
/// A paid plan at a provider. Active until the period it's paid for ends: renewed by a payment, ended by the provider
/// or, when no word comes, by <c>SubscriptionLapse</c> after a grace period.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Provider { get; set; }

    /// <summary>
    /// The provider's id for it; unique per provider.
    /// </summary>
    public required string ProviderSubscriptionId { get; set; }

    public required string Plan { get; set; }
    public required string Interval { get; set; }

    /// <summary>
    /// The price per period, from its checkout: a renewal's receipt when the provider's event doesn't state one.
    /// </summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "USD";

    public SubscriptionStatus Status { get; set; }
    public DateTime CurrentPeriodEndUtc { get; set; }

    /// <summary>
    /// Cancelled, so it ends at <see cref="CurrentPeriodEndUtc"/> instead of renewing.
    /// </summary>
    public bool CancelAtPeriodEnd { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}

public enum SubscriptionStatus
{
    Active,
    Ended
}

/// <summary>
/// A payment, the user's receipt (in their data export).
/// </summary>
public class PaymentReceipt
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public Guid SubscriptionId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderPaymentId { get; set; }
    public required string Plan { get; set; }
    public required string Interval { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTime PaidAtUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
}

/// <summary>
/// A provider's webhook event already applied: providers resend, and a payment must count once.
/// </summary>
public class ProcessedPaymentEvent
{
    public required string Provider { get; set; }
    public required string EventId { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}

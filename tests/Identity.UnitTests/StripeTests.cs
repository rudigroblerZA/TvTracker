using Identity.API.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TvTracker.Payments;

namespace Identity.UnitTests;

/// <summary>
/// Stripe's webhooks read into the app's events (CONTEXT.md, Payments): signed with the endpoint's secret, both invoice
/// shapes (before and after API version 2025-03-31), a cancellation only when that's what changed, and everything else
/// acknowledged rather than refused.
/// </summary>
public class StripeTests
{
    private const string Secret = "whsec_test";
    private const string Session = "0199a3b2-0000-7000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static StripePaymentProvider Provider() =>
        new(new HttpClient(), Options.Create(new PaymentOptions { Stripe = new StripeOptions { WebhookSecret = Secret } }));

    private static HeaderDictionary Signed(string body, string secret = Secret) =>
        new() { [StripePaymentProvider.SignatureHeader] = PaymentSignature.Sign(body, secret, Now) };

    private static string Event(string type, string obj, string previous = "") =>
        "{\"id\":\"evt_1\",\"type\":\"" + type + "\",\"data\":{\"object\":" + obj
        + (previous.Length > 0 ? ",\"previous_attributes\":" + previous : "") + "}}";

    /// <summary>
    /// API 2025-03-31 and later: the subscription and its metadata under parent.subscription_details.
    /// </summary>
    private static readonly string NewInvoice = Event("invoice.paid", """
        {"id":"in_1","object":"invoice","amount_paid":299,"currency":"usd",
         "parent":{"type":"subscription_details","subscription_details":{"subscription":"sub_1","metadata":{"checkout_session":"SESSION"}}},
         "lines":{"data":[{"period":{"start":1762603200,"end":1765195200}}]}}
        """.Replace("SESSION", Session));

    /// <summary>
    /// Earlier API versions: the subscription at the top, its metadata in subscription_details.
    /// </summary>
    private static readonly string OldInvoice = Event("invoice.paid", """
        {"id":"in_2","object":"invoice","amount_paid":2999,"currency":"usd","subscription":"sub_2",
         "subscription_details":{"metadata":{"checkout_session":"SESSION"}},
         "lines":{"data":[{"period":{"start":1762603200,"end":1794139200}}]}}
        """.Replace("SESSION", Session));

    [Fact]
    public void A_paid_invoice_names_its_subscription_checkout_amount_and_period_in_either_shape()
    {
        var e = Provider().ReadWebhook(NewInvoice, Signed(NewInvoice), Now)!;
        Assert.Equal(new PaymentEvent("evt_1", PaymentEventKind.Paid, "sub_1", Guid.Parse(Session), "in_1", 2.99m, "USD",
            DateTimeOffset.FromUnixTimeSeconds(1765195200).UtcDateTime), e);

        var old = Provider().ReadWebhook(OldInvoice, Signed(OldInvoice), Now)!;
        Assert.Equal(("sub_2", Guid.Parse(Session), 29.99m), (old.SubscriptionId, old.SessionId, old.Amount));
    }

    [Fact]
    public void Unsigned_wrongly_signed_or_stale_webhooks_are_refused()
    {
        Assert.Null(Provider().ReadWebhook(NewInvoice, new HeaderDictionary(), Now));
        Assert.Null(Provider().ReadWebhook(NewInvoice, Signed(NewInvoice, "whsec_other"), Now));
        Assert.Null(Provider().ReadWebhook(NewInvoice, Signed(NewInvoice), Now.AddMinutes(6)));
        Assert.Null(Provider().ReadWebhook(NewInvoice + " ", Signed(NewInvoice), Now));
    }

    [Fact]
    public void A_cancellation_counts_only_when_it_is_what_changed()
    {
        var scheduled = Event("customer.subscription.updated", """{"id":"sub_1","cancel_at_period_end":true,"cancel_at":null}""",
            """{"cancel_at_period_end":false}""");
        Assert.Equal(PaymentEventKind.CancelScheduled, StripePaymentProvider.Read(scheduled)!.Kind);

        // The customer portal on newer versions sets cancel_at instead.
        var portal = Event("customer.subscription.updated", """{"id":"sub_1","cancel_at_period_end":false,"cancel_at":1765195200}""",
            """{"cancel_at":null}""");
        Assert.Equal(PaymentEventKind.CancelScheduled, StripePaymentProvider.Read(portal)!.Kind);

        var resumed = Event("customer.subscription.updated", """{"id":"sub_1","cancel_at_period_end":false,"cancel_at":null}""",
            """{"cancel_at_period_end":true}""");
        Assert.Equal(PaymentEventKind.Resumed, StripePaymentProvider.Read(resumed)!.Kind);

        var renewal = Event("customer.subscription.updated", """{"id":"sub_1","cancel_at_period_end":true}""",
            """{"current_period_end":1762603200}""");
        Assert.Equal(PaymentEventKind.Ignored, StripePaymentProvider.Read(renewal)!.Kind);
    }

    [Fact]
    public void A_deleted_subscription_ends_and_other_events_are_acknowledged()
    {
        Assert.Equal(new PaymentEvent("evt_1", PaymentEventKind.Ended, "sub_1"),
            StripePaymentProvider.Read(Event("customer.subscription.deleted", """{"id":"sub_1"}""")));
        Assert.Equal(PaymentEventKind.Ignored, StripePaymentProvider.Read(Event("charge.succeeded", """{"id":"ch_1"}"""))!.Kind);
        Assert.Equal(PaymentEventKind.Ignored, StripePaymentProvider.Read(Event("invoice.paid", """{"id":"in_3","amount_paid":500,"currency":"usd"}"""))!.Kind);
        Assert.Null(StripePaymentProvider.Read("""{"id":"evt_1"}"""));
    }

    [Theory]
    [InlineData(2.99, "USD", 299)]
    [InlineData(49.99, "eur", 4999)]
    [InlineData(500, "JPY", 500)]
    public void Amounts_go_to_Stripe_in_its_minor_units(decimal amount, string currency, long minor)
    {
        Assert.Equal(minor, StripePaymentProvider.MinorUnits(amount, currency));
        Assert.Equal(amount, StripePaymentProvider.FromMinorUnits(minor, currency));
    }
}

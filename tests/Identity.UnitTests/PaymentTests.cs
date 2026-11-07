using Identity.API.Data;
using Identity.API.Payments;
using Identity.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TvTracker.Payments;
using TvTracker.IntegrationEvents;

namespace Identity.UnitTests;

/// <summary>
/// The payment rules that decide money and plans: a webhook counts only when signed and fresh, prices follow the
/// profile's region, and a subscription lapses only after its grace period.
/// </summary>
public class PaymentTests
{
    private const string Secret = "test-secret";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static TestPaymentProvider Provider() =>
        new(Options.Create(new PaymentOptions { Test = new TestProviderOptions { WebhookSecret = Secret } }));

    private static HeaderDictionary Signed(string body, DateTimeOffset at, string secret = Secret) =>
        new() { [PaymentSignature.Header] = PaymentSignature.Sign(body, secret, at) };

    private const string Paid = """{"id":"evt_1","type":"payment.succeeded","subscription":"sub_1","session":"0199a3b2-0000-7000-8000-000000000001","payment":"pay_1","amount":2.99,"currency":"USD"}""";

    [Fact]
    public void A_signed_fresh_webhook_is_read()
    {
        var e = Provider().ReadWebhook(Paid, Signed(Paid, Now.AddMinutes(-1)), Now);

        Assert.NotNull(e);
        Assert.Equal(PaymentEventKind.Paid, e.Kind);
        Assert.Equal("sub_1", e.SubscriptionId);
        Assert.Equal(2.99m, e.Amount);
        Assert.Equal(Guid.Parse("0199a3b2-0000-7000-8000-000000000001"), e.SessionId);
    }

    [Fact]
    public void An_unsigned_tampered_wrongly_signed_or_stale_webhook_is_refused()
    {
        var provider = Provider();

        Assert.Null(provider.ReadWebhook(Paid, new HeaderDictionary(), Now));
        Assert.Null(provider.ReadWebhook(Paid.Replace("2.99", "0.01"), Signed(Paid, Now), Now));
        Assert.Null(provider.ReadWebhook(Paid, Signed(Paid, Now, secret: "someone-else"), Now));
        Assert.Null(provider.ReadWebhook(Paid, Signed(Paid, Now.AddMinutes(-6)), Now));
        Assert.Null(provider.ReadWebhook(Paid, Signed(Paid, Now.AddMinutes(6)), Now));
    }

    [Fact]
    public void A_rotating_secret_signs_twice_and_either_signature_counts()
    {
        var t = Now.ToUnixTimeSeconds();
        var old = PaymentSignature.Sign(Paid, "old-secret", Now).Split(",v1=")[1];
        var current = PaymentSignature.Sign(Paid, Secret, Now).Split(",v1=")[1];
        var headers = new HeaderDictionary { [PaymentSignature.Header] = $"t={t},v1={old},v1={current}" };

        Assert.NotNull(Provider().ReadWebhook(Paid, headers, Now));
    }

    [Fact]
    public void Malformed_headers_and_bodies_are_refused_not_errors()
    {
        var provider = Provider();
        var t = Now.ToUnixTimeSeconds();
        const string notJson = "{not json";
        const string badSession = """{"id":"evt_4","type":"payment.succeeded","subscription":"sub_1","session":"not-a-guid"}""";

        Assert.Null(provider.ReadWebhook(Paid, new HeaderDictionary { [PaymentSignature.Header] = $"t={t},t={t},v1=ab" }, Now));
        Assert.Null(provider.ReadWebhook(Paid, new HeaderDictionary { [PaymentSignature.Header] = "t=99999999999999999999,v1=ab" }, Now));
        Assert.Null(provider.ReadWebhook(notJson, Signed(notJson, Now), Now));
        Assert.Null(provider.ReadWebhook(badSession, Signed(badSession, Now), Now));
    }

    [Fact]
    public void Only_the_events_the_app_acts_on_are_read()
    {
        const string ended = """{"id":"evt_2","type":"subscription.ended","subscription":"sub_1"}""";
        const string other = """{"id":"evt_3","type":"invoice.created","subscription":"sub_1"}""";

        Assert.Equal(PaymentEventKind.Ended, Provider().ReadWebhook(ended, Signed(ended, Now), Now)?.Kind);
        Assert.Null(Provider().ReadWebhook(other, Signed(other, Now), Now));
    }

    [Fact]
    public void Prices_follow_the_region_then_everywhere_then_the_default()
    {
        List<RegionPrices> configured = [new("ZA", "ZAR", 39m, 390m, 69m, 690m), new("*", "EUR", 2.79m, 27.9m, 4.59m, 45.9m)];

        Assert.Equal(39m, PlanPrices.For("ZA", configured).For(Plans.Plus, PlanPrices.Month));
        Assert.Equal("EUR", PlanPrices.For("DE", configured).Currency);
        Assert.Equal(PlanPrices.Default, PlanPrices.For("US", []));
        Assert.Equal(2.99m, PlanPrices.Default.For(Plans.Plus, PlanPrices.Month));
        Assert.Equal(49.99m, PlanPrices.Default.For(Plans.Family, PlanPrices.Year));
        Assert.Throws<ArgumentException>(() => PlanPrices.Default.For(Plans.Free, PlanPrices.Month));
    }

    [Fact]
    public void A_renewing_subscription_lapses_after_its_grace_period_and_a_cancelled_one_at_its_end()
    {
        var subscription = new Subscription
        {
            UserId = "u", Provider = "test", ProviderSubscriptionId = "sub_1", Plan = Plans.Plus, Interval = PlanPrices.Month,
            Status = SubscriptionStatus.Active, CurrentPeriodEndUtc = Now.UtcDateTime.AddDays(-2)
        };

        Assert.False(SubscriptionLapse.Lapsed(subscription, Now.UtcDateTime, graceDays: 3));
        Assert.True(SubscriptionLapse.Lapsed(subscription, Now.UtcDateTime.AddDays(2), graceDays: 3));

        // Cancelled, it ends when its period does: no renewal is coming to wait for.
        subscription.CancelAtPeriodEnd = true;
        Assert.True(SubscriptionLapse.Lapsed(subscription, Now.UtcDateTime, graceDays: 3));
        subscription.CancelAtPeriodEnd = false;
        subscription.Status = SubscriptionStatus.Ended;
        Assert.False(SubscriptionLapse.Lapsed(subscription, Now.UtcDateTime.AddDays(30), graceDays: 3));
    }

    [Fact]
    public void A_first_payment_pays_one_period_and_each_renewal_one_more()
    {
        var session = new CheckoutSession
        {
            UserId = "u", Provider = "test", Plan = Plans.Plus, Interval = PlanPrices.Month, Amount = 2.99m, Currency = "USD"
        };
        var subscription = SubscriptionLedger.Start("test", new PaymentEvent("evt_1", PaymentEventKind.Paid, "sub_1"), session, Now);

        // Bought on 7 November, it renews on 7 December (a start already a period out made it 7 January).
        var first = SubscriptionLedger.PaidThrough(subscription.CurrentPeriodEndUtc, null, PlanPrices.Month, Now.UtcDateTime);
        Assert.Equal(new DateTime(2026, 12, 7, 12, 0, 0), first);
        // Renewed a day early, the month runs on from the period's end, not from the payment.
        Assert.Equal(new DateTime(2027, 1, 7, 12, 0, 0),
            SubscriptionLedger.PaidThrough(first, null, PlanPrices.Month, first.AddDays(-1)));
        Assert.Equal(new DateTime(2027, 11, 7, 12, 0, 0),
            SubscriptionLedger.PaidThrough(Now.UtcDateTime, null, PlanPrices.Year, Now.UtcDateTime));
    }

    [Fact]
    public void A_lapsed_subscription_paid_again_starts_afresh_and_no_payment_shortens_a_period()
    {
        var lapsedAt = Now.UtcDateTime.AddDays(-20);
        Assert.Equal(Now.UtcDateTime.AddMonths(1), SubscriptionLedger.PaidThrough(lapsedAt, null, PlanPrices.Month, Now.UtcDateTime));

        // The provider's own date stands, unless it's before what's already paid (a late or reordered event).
        var paid = Now.UtcDateTime.AddDays(10);
        Assert.Equal(paid.AddDays(30), SubscriptionLedger.PaidThrough(paid, paid.AddDays(30), PlanPrices.Month, Now.UtcDateTime));
        Assert.Equal(paid, SubscriptionLedger.PaidThrough(paid, Now.UtcDateTime, PlanPrices.Month, Now.UtcDateTime));
    }
}

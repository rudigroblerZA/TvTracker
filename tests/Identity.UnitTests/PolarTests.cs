using System.Text;
using System.Text.Json;
using Identity.API.Payments;
using Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;

namespace Identity.UnitTests;

/// <summary>
/// Polar's webhooks read into the app's events (Standard Webhooks signatures, both ways Polar has made secrets), what
/// a checkout is made with, and what Polar still needs before it can sell. The payload shapes are Polar's published
/// API (version 2026-10); nothing here talks to Polar.
/// </summary>
public class PolarTests
{
    /// <summary>
    /// A Standard Webhooks secret: "whsec_" and the key, base64.
    /// </summary>
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("a-signing-key-of-some-length-0123");
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(Key);

    private const string Session = "0199a3b2-0000-7000-8000-000000000001";
    private const string Delivery = "msg_2xYz";
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static PolarPaymentProvider Provider(string? secret = null) =>
        new(new HttpClient(), Options.Create(new PaymentOptions { Polar = new PolarOptions { WebhookSecret = secret ?? Secret } }));

    private static HeaderDictionary Signed(string body, byte[]? key = null, DateTimeOffset? at = null, string id = Delivery) => new()
    {
        [PolarPaymentProvider.IdHeader] = id,
        [PolarPaymentProvider.TimestampHeader] = (at ?? Now).ToUnixTimeSeconds().ToString(),
        [PolarPaymentProvider.SignatureHeader] = PolarPaymentProvider.Sign(body, id, at ?? Now, key ?? Key)
    };

    private static string Event(string type, string data) =>
        "{\"type\":\"" + type + "\",\"timestamp\":\"2026-11-22T12:00:00Z\",\"api_version\":\"2026-10\",\"data\":" + data + "}";

    private static readonly string FirstOrder = Event("order.paid", """
        {"id":"ord_1","status":"paid","paid":true,"billing_reason":"subscription_create","total_amount":344,"net_amount":299,"tax_amount":45,
         "currency":"usd","subscription_id":"sub_1","metadata":{"checkout_session":"SESSION","plan":"plus"},
         "subscription":{"id":"sub_1","status":"active","cancel_at_period_end":false,"current_period_end":"2026-12-22T12:00:00.000000Z","metadata":{"checkout_session":"SESSION"}}}
        """.Replace("SESSION", Session));

    private static string Subscription(string status, bool cancelAtPeriodEnd) =>
        "{\"id\":\"sub_1\",\"status\":\"" + status + "\",\"cancel_at_period_end\":" + (cancelAtPeriodEnd ? "true" : "false")
        + ",\"current_period_end\":\"2026-12-22T12:00:00Z\"}";

    [Fact]
    public void A_first_payment_names_its_checkout_and_what_was_paid_tax_included()
    {
        var e = Provider().ReadWebhook(FirstOrder, Signed(FirstOrder), Now);

        Assert.NotNull(e);
        Assert.Equal(PaymentEventKind.Paid, e.Kind);
        // Polar's payload has no event id: the delivery's is the event's, and stays the same when Polar retries.
        Assert.Equal(Delivery, e.Id);
        Assert.Equal("sub_1", e.SubscriptionId);
        Assert.Equal(Guid.Parse(Session), e.SessionId);
        Assert.Equal("ord_1", e.PaymentId);
        Assert.Equal(3.44m, e.Amount);
        Assert.Equal("USD", e.Currency);
        Assert.Equal(new DateTime(2026, 12, 22, 12, 0, 0, DateTimeKind.Utc), e.PeriodEndUtc);
    }

    [Fact]
    public void A_renewal_finds_the_checkout_on_its_subscription()
    {
        var body = Event("order.paid", """
            {"id":"ord_2","billing_reason":"subscription_cycle","total_amount":299,"currency":"usd","subscription_id":"sub_1","metadata":{},
             "subscription":{"id":"sub_1","current_period_end":"2027-01-22T12:00:00Z","metadata":{"checkout_session":"SESSION"}}}
            """.Replace("SESSION", Session));

        var e = Provider().ReadWebhook(body, Signed(body), Now);

        Assert.Equal(Guid.Parse(Session), e!.SessionId);
        Assert.Equal(2.99m, e.Amount);
    }

    [Fact]
    public void An_order_that_is_not_for_a_subscription_is_acknowledged_and_ignored()
    {
        var body = Event("order.paid", """{"id":"ord_3","billing_reason":"purchase","total_amount":500,"currency":"usd","subscription_id":null}""");

        Assert.Equal(PaymentEventKind.Ignored, Provider().ReadWebhook(body, Signed(body), Now)!.Kind);
    }

    [Theory]
    [InlineData("subscription.canceled", "active", true, PaymentEventKind.CancelScheduled)]
    // Ended on the spot: "revoked" follows and is what ends it.
    [InlineData("subscription.canceled", "canceled", false, PaymentEventKind.Ignored)]
    [InlineData("subscription.uncanceled", "active", false, PaymentEventKind.Resumed)]
    [InlineData("subscription.revoked", "canceled", false, PaymentEventKind.Ended)]
    [InlineData("subscription.updated", "active", true, PaymentEventKind.Ignored)]
    [InlineData("subscription.past_due", "past_due", false, PaymentEventKind.Ignored)]
    public void A_subscription_event_is_read_for_what_it_changes(string type, string status, bool cancelAtPeriodEnd, PaymentEventKind kind)
    {
        var body = Event(type, Subscription(status, cancelAtPeriodEnd));

        var e = Provider().ReadWebhook(body, Signed(body), Now);

        Assert.Equal(kind, e!.Kind);
        if (kind != PaymentEventKind.Ignored)
        {
            Assert.Equal("sub_1", e.SubscriptionId);
        }
    }

    [Fact]
    public void A_secret_of_polars_earlier_kind_signs_with_its_own_text()
    {
        const string old = "polar_whs_plain-text-secret";

        Assert.NotNull(Provider(old).ReadWebhook(FirstOrder, Signed(FirstOrder, Encoding.UTF8.GetBytes(old)), Now));
    }

    [Fact]
    public void One_good_signature_among_several_is_enough()
    {
        var headers = Signed(FirstOrder);
        headers[PolarPaymentProvider.SignatureHeader] = "v1,bm90LXRoZS1vbmU= " + headers[PolarPaymentProvider.SignatureHeader];

        Assert.NotNull(Provider().ReadWebhook(FirstOrder, headers, Now));
    }

    [Fact]
    public void A_webhook_is_refused_when_its_signature_body_time_or_id_is_off()
    {
        var provider = Provider();

        Assert.Null(provider.ReadWebhook(FirstOrder, Signed(FirstOrder, Encoding.UTF8.GetBytes("another key")), Now));
        Assert.Null(provider.ReadWebhook(FirstOrder + " ", Signed(FirstOrder), Now));
        Assert.Null(provider.ReadWebhook(FirstOrder, Signed(FirstOrder, at: Now.AddMinutes(-6)), Now));
        // Signed for one delivery id, presented under another.
        var moved = Signed(FirstOrder);
        moved[PolarPaymentProvider.IdHeader] = "msg_other";
        Assert.Null(provider.ReadWebhook(FirstOrder, moved, Now));
        Assert.Null(provider.ReadWebhook(FirstOrder, new HeaderDictionary(), Now));
        Assert.Null(Provider("").ReadWebhook(FirstOrder, Signed(FirstOrder), Now));
    }

    [Fact]
    public void A_signed_payload_that_cannot_be_read_is_refused()
    {
        const string body = """{"type":"order.paid"}""";

        Assert.Null(Provider().ReadWebhook(body, Signed(body), Now));
    }

    [Fact]
    public void A_checkout_is_for_the_plans_product_at_the_sessions_own_price()
    {
        var session = new CheckoutSession
        {
            Id = Guid.Parse(Session),
            UserId = "user-1",
            Provider = "polar",
            Plan = Plans.Family,
            Interval = PlanPrices.Year,
            Amount = 49.99m,
            Currency = "ZAR"
        };

        var body = JsonSerializer.SerializeToElement(
            PolarPaymentProvider.CheckoutBody(session, "prod_family_year", new Uri("https://tv.example.com/settings/plan")));

        Assert.Equal("prod_family_year", body.GetProperty("products")[0].GetString());
        var price = body.GetProperty("prices").GetProperty("prod_family_year")[0];
        Assert.Equal("fixed", price.GetProperty("amount_type").GetString());
        Assert.Equal(4999, price.GetProperty("price_amount").GetInt64());
        Assert.Equal("zar", price.GetProperty("price_currency").GetString());
        Assert.Equal("https://tv.example.com/settings/plan?checkout=done", body.GetProperty("success_url").GetString());
        Assert.Equal("user-1", body.GetProperty("external_customer_id").GetString());
        Assert.Equal(Session, body.GetProperty("metadata").GetProperty(PolarPaymentProvider.SessionKey).GetString());
    }

    [Fact]
    public void Polar_sells_once_it_has_its_token_its_secret_a_return_address_and_all_four_products()
    {
        var options = new PaymentOptions { Provider = "polar", ReturnUrl = "https://tv.example.com/settings/plan" };
        Assert.Equal(6, PaymentOptionsValidator.Missing(options).Count);

        options.Polar = new PolarOptions
        {
            AccessToken = "polar_oat_x",
            WebhookSecret = Secret,
            Products = new PolarProducts { PlusMonth = "a", PlusYear = "b", FamilyMonth = "c", FamilyYear = " " }
        };
        Assert.Contains("Family by the year", Assert.Single(PaymentOptionsValidator.Missing(options)));

        options.Polar.Products.FamilyYear = "d";
        Assert.True(PaymentOptionsValidator.Sells(options));
        Assert.True(new PaymentOptionsValidator().Validate(null, options).Succeeded);
    }
}

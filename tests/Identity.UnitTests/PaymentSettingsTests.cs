using Identity.API.Apis;
using Identity.API.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Identity.UnitTests;

/// <summary>
/// The payment provider as an admin sets it: what is in force when their settings lie over configuration, how the
/// keys are kept, and which changes are refused.
/// </summary>
public class PaymentSettingsTests
{
    private static SettingsCipher Cipher(string? key = "a-deployment-secret-of-its-own") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:SettingsKey"] = key }).Build());

    private static PaymentOptions Configured(string? provider = "stripe", string? secretKey = null, string? webhookSecret = null) => new()
    {
        Provider = provider,
        ReturnUrl = "https://tv.example/settings/plan",
        Stripe = new StripeOptions { SecretKey = secretKey, WebhookSecret = webhookSecret, PortalUrl = "https://billing.stripe.com/p/login/configured" }
    };

    private static PaymentProviders Providers(PaymentOptions options) =>
        new([new StripePaymentProvider(new HttpClient(), Options.Create(options)), new TestPaymentProvider(Options.Create(options))], Options.Create(options));

    [Fact]
    public void A_key_comes_back_only_under_the_key_it_was_saved_with()
    {
        var stored = Cipher().Protect("sk_live_123");

        Assert.DoesNotContain("sk_live_123", stored);
        Assert.Equal("sk_live_123", Cipher().Unprotect(stored));
        Assert.Null(Cipher("another-deployment-secret").Unprotect(stored));
        Assert.Null(Cipher(null).Unprotect(stored));
        Assert.Null(Cipher().Unprotect("v1:not-base64!"));
        Assert.Null(Cipher().Unprotect("sk_live_123"));
    }

    [Fact]
    public void The_same_key_is_stored_differently_each_time() => Assert.NotEqual(Cipher().Protect("sk_live_123"), Cipher().Protect("sk_live_123"));

    [Fact]
    public void Without_a_settings_key_nothing_secret_can_be_saved()
    {
        Assert.False(Cipher(null).Available);
        Assert.False(Cipher("short").Available);
        Assert.Throws<InvalidOperationException>(() => Cipher(null).Protect("sk_live_123"));
    }

    [Fact]
    public void What_an_admin_set_lies_over_configuration_field_by_field()
    {
        var configured = Configured(secretKey: "sk_configured", webhookSecret: "whsec_configured");

        var merged = EffectivePaymentOptions.Merge(configured, new StoredPaymentSettings(null, "sk_set", null, null));

        Assert.Equal(("stripe", "sk_set", "whsec_configured", "https://billing.stripe.com/p/login/configured"),
            (merged.Provider, merged.Stripe.SecretKey, merged.Stripe.WebhookSecret, merged.Stripe.PortalUrl));
        Assert.Equal(configured.ReturnUrl, merged.ReturnUrl);
        Assert.Same(configured, EffectivePaymentOptions.Merge(configured, null));
    }

    [Fact]
    public void An_admin_can_stop_selling_whatever_configuration_says() =>
        Assert.Equal("", EffectivePaymentOptions.Merge(Configured(secretKey: "sk_x", webhookSecret: "whsec_x"), new StoredPaymentSettings("", null, null, null)).Provider);

    [Fact]
    public void Stripe_without_its_keys_starts_but_doesnt_sell()
    {
        var options = Configured();

        // The service starts: an admin can still supply the keys.
        Assert.True(new PaymentOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(2, PaymentOptionsValidator.Missing(options).Count);
        Assert.Null(Providers(options).Selling);
    }

    [Fact]
    public void Stripe_with_both_keys_sells()
    {
        var options = Configured(secretKey: "sk_live_1", webhookSecret: "whsec_1");

        Assert.Empty(PaymentOptionsValidator.Missing(options));
        Assert.Equal("stripe", Providers(options).Selling?.Name);
    }

    [Fact]
    public void Nothing_is_missing_when_nothing_is_sold() => Assert.Empty(PaymentOptionsValidator.Missing(Configured(provider: "")));

    [Theory]
    [InlineData("paddle", null, null, null, "Choose a provider")]
    [InlineData("stripe", "pk_live_1", null, null, "starts with sk_")]
    [InlineData("stripe", null, "secret", null, "starts with whsec_")]
    [InlineData("stripe", null, null, "http://billing.stripe.com/x", "https address")]
    [InlineData("stripe", null, null, "not a link", "https address")]
    public void A_request_that_cant_be_right_is_refused(string? provider, string? secretKey, string? webhookSecret, string? portal, string expected) =>
        Assert.Contains(expected, PaymentSettingsApi.Problem(provider, secretKey, webhookSecret, portal, Providers(Configured()), Cipher()));

    [Theory]
    [InlineData("stripe", "sk_live_1", "whsec_1", "https://billing.stripe.com/p/login/x")]
    [InlineData("stripe", "rk_live_1", null, "")]
    [InlineData("", null, null, null)]
    [InlineData(null, null, null, null)]
    public void A_sound_request_passes(string? provider, string? secretKey, string? webhookSecret, string? portal) =>
        Assert.Null(PaymentSettingsApi.Problem(provider, secretKey, webhookSecret, portal, Providers(Configured()), Cipher()));

    [Fact]
    public void Keys_are_refused_where_they_couldnt_be_encrypted() =>
        Assert.Contains("Payments:SettingsKey", PaymentSettingsApi.Problem("stripe", "sk_live_1", null, null, Providers(Configured()), Cipher(null)));
}

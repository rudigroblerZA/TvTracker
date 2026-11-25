using Financials.API.Apis;
using Financials.API.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Financials.UnitTests;

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
        Assert.False(Cipher("").Available);
        Assert.Throws<InvalidOperationException>(() => Cipher(null).Protect("sk_live_123"));
    }

    [Fact]
    public void A_settings_key_too_short_to_be_one_stops_the_service_instead_of_being_ignored() =>
        Assert.Contains("at least 16", Assert.Throws<InvalidOperationException>(() => Cipher("short")).Message);

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

    [Theory]
    [InlineData("https://tv.example/settings/plan", "https://tv.example/api/financials/api/v1/payments/webhooks/stripe")]
    [InlineData("/settings/plan", null)]
    [InlineData(null, null)]
    public void The_webhook_address_is_given_only_where_the_sites_address_is_known(string? returnUrl, string? webhook) =>
        Assert.Equal(webhook, PaymentSettingsApi.PublicWebhookUrl(returnUrl, "stripe"));

    private static readonly PaymentOptions SellingStripe = Configured(secretKey: "sk_live_1", webhookSecret: "whsec_1");
    private static readonly PaymentOptions StripeNoKeys = Configured();
    private static readonly PaymentOptions StripeOneKey = Configured(secretKey: "sk_live_1");
    private static readonly PaymentOptions Nothing = Configured(provider: "");

    [Fact]
    public void A_provider_that_isnt_selling_yet_can_be_set_up_a_key_at_a_time() =>
        // The webhook secret only exists once the endpoint is registered at Stripe, so the secret key comes first.
        Assert.Null(PaymentSettingsApi.Refusal(StripeNoKeys, StripeOneKey, choosesProvider: false));

    [Fact]
    public void Taking_a_key_from_the_provider_that_sells_is_refused() =>
        Assert.Contains("would stop plans being sold", PaymentSettingsApi.Refusal(SellingStripe, StripeOneKey, choosesProvider: false));

    [Fact]
    public void Going_back_to_a_configuration_without_the_keys_is_refused_while_selling() =>
        Assert.Contains("webhook signing secret", PaymentSettingsApi.Refusal(SellingStripe, StripeNoKeys, choosesProvider: false));

    [Fact]
    public void Choosing_a_provider_that_couldnt_sell_is_refused_even_when_nothing_is_sold() =>
        Assert.NotNull(PaymentSettingsApi.Refusal(Nothing, StripeOneKey, choosesProvider: true));

    [Fact]
    public void Stopping_sales_on_purpose_is_allowed() => Assert.Null(PaymentSettingsApi.Refusal(SellingStripe, Nothing, choosesProvider: false));

    [Fact]
    public void Going_back_to_a_configuration_with_no_provider_is_refused_while_plans_sell() =>
        Assert.Contains("names no provider", PaymentSettingsApi.ClearRefusal(SellingStripe, Nothing));

    [Fact]
    public void Going_back_to_a_configuration_that_sells_is_allowed() => Assert.Null(PaymentSettingsApi.ClearRefusal(SellingStripe, SellingStripe));

    [Fact]
    public void Going_back_is_allowed_when_nothing_sells_now() => Assert.Null(PaymentSettingsApi.ClearRefusal(Nothing, Nothing));

    [Fact]
    public void A_change_that_leaves_the_provider_selling_is_allowed() =>
        Assert.Null(PaymentSettingsApi.Refusal(SellingStripe, Configured(secretKey: "sk_live_2", webhookSecret: "whsec_1"), choosesProvider: true));

    [Theory]
    [InlineData("sk_live_ключ")]
    [InlineData("sk_live_with space")]
    public void A_key_with_characters_Stripe_doesnt_use_is_refused(string key) =>
        Assert.Contains("letters, digits and underscores", PaymentSettingsApi.Problem("stripe", key, null, null, Providers(Configured()), Cipher()));

    [Fact]
    public void A_key_longer_than_Stripe_makes_them_is_refused() =>
        Assert.NotNull(PaymentSettingsApi.Problem("stripe", "sk_" + new string('a', 300), null, null, Providers(Configured()), Cipher()));

    [Fact]
    public void A_long_username_is_cut_to_what_the_column_holds()
    {
        static System.Security.Claims.ClaimsPrincipal Named(string name) =>
            new(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("preferred_username", name)]));

        Assert.Equal("admin", PaymentSettingsApi.Admin(Named("admin")));
        Assert.Equal(Financials.Data.PaymentSettings.UpdatedByLength, PaymentSettingsApi.Admin(Named(new string('a', 255)))!.Length);
        Assert.Null(PaymentSettingsApi.Admin(new System.Security.Claims.ClaimsPrincipal()));
    }
}

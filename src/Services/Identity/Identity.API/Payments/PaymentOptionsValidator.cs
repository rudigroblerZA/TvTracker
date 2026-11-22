using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;

namespace Identity.API.Payments;

/// <summary>
/// Fails startup on payment configuration that can't be right whatever an admin sets later: an unknown provider, an
/// address that isn't one, a price without a currency. What the selling provider still lacks (<see cref="Missing"/>:
/// Stripe's keys, the test provider's secret) doesn't stop the service, since an admin can supply Stripe's on the
/// Financials page; until then plans aren't sold, and the service says so when it starts. No provider means plans
/// aren't sold, which is valid.
/// </summary>
public sealed class PaymentOptionsValidator : IValidateOptions<PaymentOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentOptions options)
    {
        var errors = new List<string>();

        switch (options.Provider)
        {
            case null or "" or "test":
                break;
            case "stripe":
                if (!IsAbsoluteHttp(options.ReturnUrl))
                {
                    errors.Add("Payments:ReturnUrl must be an absolute http(s) address when Payments:Provider is stripe.");
                }
                if (!IsAbsoluteHttp(options.Stripe.ApiBase))
                {
                    errors.Add("Payments:Stripe:ApiBase must be an absolute http(s) address.");
                }
                break;
            case "polar":
                if (!IsAbsoluteHttp(options.Polar.ApiBase))
                {
                    errors.Add("Payments:Polar:ApiBase must be an absolute http(s) address.");
                }
                break;
            default:
                errors.Add($"Payments:Provider '{options.Provider}' isn't a known provider (stripe, polar, test).");
                break;
        }

        foreach (var price in options.Prices)
        {
            if (string.IsNullOrWhiteSpace(price.Currency))
            {
                errors.Add($"Payments:Prices for region '{price.Region}' has no currency.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Whether these options sell plans: a provider is named and has all it needs.
    /// </summary>
    public static bool Sells(PaymentOptions options) => options.Provider is { Length: > 0 } && Missing(options).Count == 0;

    /// <summary>
    /// What the provider that sells still needs before it can: empty when it can sell, or when nothing is sold.
    /// </summary>
    public static IReadOnlyList<string> Missing(PaymentOptions options)
    {
        var missing = new List<string>();
        switch (options.Provider)
        {
            case "stripe":
                Require(missing, options.Stripe.SecretKey, "Stripe's secret key");
                Require(missing, options.Stripe.WebhookSecret, "Stripe's webhook signing secret");
                if (!IsAbsoluteHttp(options.ReturnUrl))
                {
                    missing.Add("Payments:ReturnUrl isn't an absolute address.");
                }
                break;
            case "polar":
                Require(missing, options.Polar.AccessToken, "Polar's access token (Payments:Polar:AccessToken)");
                Require(missing, options.Polar.WebhookSecret, "Polar's webhook signing secret (Payments:Polar:WebhookSecret)");
                foreach (var plan in new[] { Plans.Plus, Plans.Family })
                {
                    foreach (var interval in PlanPrices.Intervals)
                    {
                        if (options.Polar.Products.For(plan, interval) is null)
                        {
                            missing.Add($"Polar's product for {Plans.Name(plan)} by the {interval} (Payments:Polar:Products) isn't set.");
                        }
                    }
                }
                if (!IsAbsoluteHttp(options.ReturnUrl))
                {
                    missing.Add("Payments:ReturnUrl isn't an absolute address.");
                }
                break;
            case "test":
                Require(missing, options.Test.WebhookSecret, "The test provider's webhook secret (Payments:Test:WebhookSecret)");
                break;
        }
        return missing;
    }

    private static void Require(List<string> errors, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} isn't set.");
        }
    }

    private static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

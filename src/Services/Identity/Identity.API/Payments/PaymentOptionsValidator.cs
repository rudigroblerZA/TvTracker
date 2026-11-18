using Microsoft.Extensions.Options;

namespace Identity.API.Payments;

/// <summary>
/// Fails startup when the provider that sells plans lacks what it needs, so a missing Stripe key or return address
/// shows at deploy time rather than when the first buyer checks out. No provider means plans aren't sold, which is valid.
/// </summary>
public sealed class PaymentOptionsValidator : IValidateOptions<PaymentOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentOptions options)
    {
        var errors = new List<string>();

        switch (options.Provider)
        {
            case null or "":
                break;
            case "stripe":
                Require(errors, options.Stripe.SecretKey, "Payments:Stripe:SecretKey");
                Require(errors, options.Stripe.WebhookSecret, "Payments:Stripe:WebhookSecret");
                if (!IsAbsoluteHttp(options.ReturnUrl))
                {
                    errors.Add("Payments:ReturnUrl must be an absolute http(s) address when Payments:Provider is stripe.");
                }
                if (!IsAbsoluteHttp(options.Stripe.ApiBase))
                {
                    errors.Add("Payments:Stripe:ApiBase must be an absolute http(s) address.");
                }
                break;
            case "test":
                Require(errors, options.Test.WebhookSecret, "Payments:Test:WebhookSecret");
                break;
            default:
                errors.Add($"Payments:Provider '{options.Provider}' isn't a known provider (stripe, test).");
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

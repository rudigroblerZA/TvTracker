using System.Security.Claims;
using Identity.API.Data;
using Identity.API.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Identity.API.Apis;

/// <summary>
/// How payments are set up, for the admins' Financials page. Secrets are never sent: only whether each is set.
/// </summary>
/// <param name="Provider">The provider in force: "" when nothing is sold.</param>
/// <param name="ProviderFrom">"settings" when an admin chose it here, else "configuration".</param>
/// <param name="Providers">The providers this service can sell through.</param>
/// <param name="StripeSecretKey">"settings", "configuration" or "" (not set): where the key in force comes from.</param>
/// <param name="StripeWebhookSecret">Likewise.</param>
/// <param name="Missing">What the provider still needs before plans can be bought; empty when it can sell.</param>
/// <param name="WebhookUrl">Where Stripe sends its events, when the site's public address is known (a deployment); else null.</param>
/// <param name="CanStoreSecrets">False without <c>Payments:SettingsKey</c>: keys can then only come from configuration.</param>
/// <param name="HasStored">Something was set here, so there is something to go back to configuration from.</param>
public record PaymentSettingsDto(string Provider, string ProviderFrom, IReadOnlyList<string> Providers, string StripeSecretKey,
    string StripeWebhookSecret, string? StripePortalUrl, string StripePortalUrlFrom, string? ReturnUrl, IReadOnlyList<string> Missing,
    string? WebhookUrl, bool CanStoreSecrets, bool HasStored, DateTime? UpdatedAtUtc, string? UpdatedBy);

/// <param name="Provider">Null leaves it as it is; "" sells nothing; else a provider's name.</param>
/// <param name="StripeSecretKey">Null or empty leaves the stored key as it is.</param>
/// <param name="StripeWebhookSecret">Likewise.</param>
/// <param name="StripePortalUrl">Null leaves it; "" goes back to configuration.</param>
public record SavePaymentSettingsRequest(string? Provider, string? StripeSecretKey, string? StripeWebhookSecret, string? StripePortalUrl);

/// <summary>
/// The payment provider, set by admins: which one sells, and Stripe's keys. What is saved here lies over configuration
/// (<see cref="EffectivePaymentOptions"/>) and takes effect at once on this instance. Anyone but an admin gets 404.
/// <list type="bullet">
/// <item><b>Keys go in, never out.</b> They are stored encrypted (<see cref="SettingsCipher"/>) and the answer only says
/// where each comes from.</item>
/// <item><b>A change that would stop sales is refused</b> (<see cref="Refusal"/>): choosing a provider that lacks a
/// key, or taking a key away from the one selling, by a save or by going back to configuration. A provider that
/// isn't selling yet can be set up a field at a time, and "none" stops sales on purpose.</item>
/// </list>
/// </summary>
public static class PaymentSettingsApi
{
    /// <summary>
    /// Stripe's webhook as the outside world reaches it in a deployment: the site's address, the gateway under /api,
    /// its identity route, then this service's own (<see cref="PaymentsApi"/>).
    /// </summary>
    private const string PublicWebhookPath = "/api/identity/api/v1/payments/webhooks/stripe";

    public static void MapPaymentSettingsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/admin/payments/settings").RequireAuthorization().RequireAppAdmin();
        api.MapGet("/", (EffectivePaymentOptions options, PaymentProviders providers, SettingsCipher cipher) => Describe(options, providers, cipher));
        api.MapPut("/", SaveAsync);
        api.MapDelete("/", ClearAsync);
    }

    private static async Task<Results<Ok<PaymentSettingsDto>, ValidationProblem>> SaveAsync(SavePaymentSettingsRequest request, ClaimsPrincipal user,
        IdentityContext db, EffectivePaymentOptions options, PaymentProviders providers, SettingsCipher cipher, TimeProvider time,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var provider = request.Provider?.Trim().ToLowerInvariant();
        var secretKey = string.IsNullOrWhiteSpace(request.StripeSecretKey) ? null : request.StripeSecretKey.Trim();
        var webhookSecret = string.IsNullOrWhiteSpace(request.StripeWebhookSecret) ? null : request.StripeWebhookSecret.Trim();
        var portal = request.StripePortalUrl?.Trim();

        var row = await db.PaymentSettings.SingleOrDefaultAsync(ct);
        // What the row will hold: a field the request leaves alone keeps what it has.
        var newProvider = provider ?? row?.Provider;
        var newPortal = portal is null ? row?.StripePortalUrl : portal.Length == 0 ? null : portal;
        // Before and after from the row just read, keys included: the copy in memory can be a minute behind another
        // instance's change.
        var before = options.Read(row);
        var after = new StoredPaymentSettings(newProvider, secretKey ?? before?.StripeSecretKey, webhookSecret ?? before?.StripeWebhookSecret, newPortal);
        var problem = Problem(provider, secretKey, webhookSecret, portal, providers, cipher)
            ?? Refusal(EffectivePaymentOptions.Merge(options.Configured, before), EffectivePaymentOptions.Merge(options.Configured, after),
                choosesProvider: provider is { Length: > 0 });
        if (problem is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["payments"] = [problem] });
        }

        if (row is null)
        {
            // Two first saves at once would both add the one row: whichever is second finds it there instead.
            var now = time.GetUtcNow().UtcDateTime;
            await db.Database.ExecuteSqlAsync(
                $"insert into payment_settings (\"Id\", \"UpdatedAtUtc\") values ({PaymentSettings.OnlyId}, {now}) on conflict (\"Id\") do nothing", ct);
            row = await db.PaymentSettings.SingleAsync(ct);
        }
        (row.Provider, row.StripePortalUrl) = (newProvider, newPortal);
        row.StripeSecretKey = secretKey is null ? row.StripeSecretKey : cipher.Protect(secretKey);
        row.StripeWebhookSecret = webhookSecret is null ? row.StripeWebhookSecret : cipher.Protect(webhookSecret);
        (row.UpdatedAtUtc, row.UpdatedBy) = (time.GetUtcNow().UtcDateTime, user.FindFirstValue("preferred_username"));
        await db.SaveChangesAsync(ct);
        await options.ReloadAsync(db, ct);
        // Who and what, never the values.
        loggers.CreateLogger(typeof(PaymentSettingsApi)).LogInformation(
            "Payment settings saved by {Admin}: provider {Provider}, secret key {SecretKey}, webhook secret {WebhookSecret}",
            row.UpdatedBy, provider ?? "(unchanged)", secretKey is null ? "unchanged" : "replaced", webhookSecret is null ? "unchanged" : "replaced");
        return TypedResults.Ok(Describe(options, providers, cipher));
    }

    /// <summary>
    /// Back to configuration alone: the stored settings, keys included, are deleted.
    /// </summary>
    private static async Task<Results<Ok<PaymentSettingsDto>, ValidationProblem>> ClearAsync(ClaimsPrincipal user, IdentityContext db,
        EffectivePaymentOptions options, PaymentProviders providers, SettingsCipher cipher, ILoggerFactory loggers, CancellationToken ct)
    {
        // The keys go with the row: configuration alone must be able to carry on selling.
        var before = options.Read(await db.PaymentSettings.AsNoTracking().SingleOrDefaultAsync(ct));
        if (Refusal(EffectivePaymentOptions.Merge(options.Configured, before), options.Configured, choosesProvider: false) is { } refused)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["payments"] = [$"{refused} The deployment's configuration doesn't have it. To stop selling on purpose, choose \"None\" first."]
            });
        }
        await db.PaymentSettings.ExecuteDeleteAsync(ct);
        await options.ReloadAsync(db, ct);
        loggers.CreateLogger(typeof(PaymentSettingsApi)).LogInformation("Payment settings cleared by {Admin}; configuration is in force",
            user.FindFirstValue("preferred_username"));
        return TypedResults.Ok(Describe(options, providers, cipher));
    }

    /// <summary>
    /// Why a change is refused for what it would do to sales, or null. Refused: plans are sold now and wouldn't be
    /// after, though a provider is still chosen; or the change itself chooses a provider that couldn't sell. Not
    /// refused: choosing none, and adding to a provider that isn't selling yet (its keys arrive one at a time).
    /// </summary>
    internal static string? Refusal(PaymentOptions before, PaymentOptions after, bool choosesProvider)
    {
        var missing = PaymentOptionsValidator.Missing(after);
        var sellsNow = before.Provider is { Length: > 0 } && PaymentOptionsValidator.Missing(before).Count == 0;
        return missing.Count > 0 && (sellsNow || choosesProvider)
            ? $"That would stop plans being sold. Still needed: {string.Join(" ", missing)}"
            : null;
    }

    /// <summary>
    /// What's wrong with a request on its own, or null.
    /// </summary>
    internal static string? Problem(string? provider, string? secretKey, string? webhookSecret, string? portal, PaymentProviders providers,
        SettingsCipher cipher) =>
        provider is { Length: > 0 } && !providers.Names.Contains(provider, StringComparer.OrdinalIgnoreCase)
            ? $"Choose a provider this service has: {string.Join(", ", providers.Names.Order())}, or none."
        : (secretKey ?? webhookSecret) is not null && !cipher.Available
            ? "Keys can't be saved here: this deployment has no Payments:SettingsKey to encrypt them with. Set them in configuration instead."
        : secretKey is not null && !(secretKey.StartsWith("sk_", StringComparison.Ordinal) || secretKey.StartsWith("rk_", StringComparison.Ordinal))
            ? "Stripe's secret key starts with sk_ (or rk_ for a restricted key)."
        : secretKey is { Length: > 255 } || webhookSecret is { Length: > 255 } ? "That key is too long to be one of Stripe's."
        : webhookSecret is not null && !webhookSecret.StartsWith("whsec_", StringComparison.Ordinal) ? "Stripe's webhook signing secret starts with whsec_."
        : portal is { Length: > 0 } && !(Uri.TryCreate(portal, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && portal.Length <= 512)
            ? "The customer portal link is an https address."
        : null;

    private static PaymentSettingsDto Describe(EffectivePaymentOptions options, PaymentProviders providers, SettingsCipher cipher)
    {
        var (current, configured, stored) = (options.Value, options.Configured, options.Stored);
        return new PaymentSettingsDto(
            current.Provider ?? "",
            stored?.Provider is null ? From.Configuration : From.Settings,
            providers.Names.Order().ToList(),
            Source(stored?.StripeSecretKey, configured.Stripe.SecretKey),
            Source(stored?.StripeWebhookSecret, configured.Stripe.WebhookSecret),
            current.Stripe.PortalUrl,
            Source(stored?.StripePortalUrl, configured.Stripe.PortalUrl),
            current.ReturnUrl,
            PaymentOptionsValidator.Missing(current),
            // The site's public address is the one buyers are sent back to, where that is a whole address.
            PublicWebhookUrl(current.ReturnUrl),
            cipher.Available,
            stored is not null,
            stored?.UpdatedAtUtc,
            stored?.UpdatedBy);
    }

    /// <summary>
    /// Null unless <paramref name="returnUrl"/> is a web address: a path alone (development) says nothing of the site's.
    /// </summary>
    internal static string? PublicWebhookUrl(string? returnUrl) =>
        Uri.TryCreate(returnUrl, UriKind.Absolute, out var site) && site.Scheme is "http" or "https"
            ? site.GetLeftPart(UriPartial.Authority) + PublicWebhookPath
            : null;

    private static string Source(string? stored, string? configured) =>
        !string.IsNullOrEmpty(stored) ? From.Settings : !string.IsNullOrEmpty(configured) ? From.Configuration : "";

    private static class From
    {
        public const string Settings = "settings";
        public const string Configuration = "configuration";
    }
}

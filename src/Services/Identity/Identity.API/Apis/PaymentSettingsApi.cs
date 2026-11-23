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
/// <param name="WebhookUrls">Where each provider sends its events, by provider name, when the site's public address is known
/// (a deployment); else empty.</param>
/// <param name="CanStoreSecrets">False without <c>Payments:SettingsKey</c>: keys can then only come from configuration.</param>
/// <param name="HasStored">Something was set here, so there is something to go back to configuration from.</param>
/// <param name="KeysUnreadable">A key saved here can't be decrypted (the deployment's settings key changed): enter it again.</param>
public record PaymentSettingsDto(string Provider, string ProviderFrom, IReadOnlyList<string> Providers, string StripeSecretKey,
    string StripeWebhookSecret, string? StripePortalUrl, string StripePortalUrlFrom, string? ReturnUrl, IReadOnlyList<string> Missing,
    IReadOnlyDictionary<string, string> WebhookUrls, bool CanStoreSecrets, bool HasStored, DateTime? UpdatedAtUtc, string? UpdatedBy,
    bool KeysUnreadable = false);

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
    /// A provider's webhook as the outside world reaches it in a deployment: the site's address, the gateway under
    /// /api, its identity route, then this service's own (<see cref="PaymentsApi"/>), which ends in the provider's name.
    /// </summary>
    private const string PublicWebhookPath = "/api/identity/api/v1/payments/webhooks/";

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
        var admin = Admin(user);
        // What's wrong with the request on its own needs no lock, and no row made.
        if (Problem(provider, secretKey, webhookSecret, portal, providers, cipher) is { } malformed)
        {
            return Refused(malformed);
        }

        // Nothing asked for, nothing recorded: a Save with no change mustn't leave a row that says an admin set something.
        if (provider is null && secretKey is null && webhookSecret is null && portal is null)
        {
            return TypedResults.Ok(Describe(options, providers, cipher));
        }

        // Read, check and write as one: the row is locked for the length of it, so a save or a clear by someone else
        // at the same moment waits, and is then checked against what this one left.
        var problem = await InTransactionAsync(db, async () =>
        {
            // The one row, made if it isn't there (inside the transaction, so a refused first save leaves none).
            var now = time.GetUtcNow().UtcDateTime;
            await db.Database.ExecuteSqlAsync(
                $"insert into payment_settings (\"Id\", \"UpdatedAtUtc\") values ({PaymentSettings.OnlyId}, {now}) on conflict (\"Id\") do nothing", ct);
            var row = await Locked(db).SingleAsync(ct);

            // What the row will hold: a field the request leaves alone keeps what it has.
            var newProvider = provider ?? row.Provider;
            var newPortal = portal is null ? row.StripePortalUrl : portal.Length == 0 ? null : portal;
            // Before and after from the row just read, keys included: the copy in memory can be a minute behind
            // another instance's change.
            var before = options.Read(row)!;
            var after = new StoredPaymentSettings(newProvider, secretKey ?? before.StripeSecretKey, webhookSecret ?? before.StripeWebhookSecret, newPortal);
            if (Refusal(EffectivePaymentOptions.Merge(options.Configured, before), EffectivePaymentOptions.Merge(options.Configured, after),
                    choosesProvider: provider is { Length: > 0 }) is { } refused)
            {
                return refused;
            }

            (row.Provider, row.StripePortalUrl) = (newProvider, newPortal);
            row.StripeSecretKey = secretKey is null ? row.StripeSecretKey : cipher.Protect(secretKey);
            row.StripeWebhookSecret = webhookSecret is null ? row.StripeWebhookSecret : cipher.Protect(webhookSecret);
            (row.UpdatedAtUtc, row.UpdatedBy) = (now, admin);
            await db.SaveChangesAsync(ct);
            return null;
        }, ct);
        if (problem is not null)
        {
            return Refused(problem);
        }

        await options.ReloadAsync(db, ct);
        // Who and what, never the values.
        loggers.CreateLogger(typeof(PaymentSettingsApi)).LogInformation(
            "Payment settings saved by {Admin}: provider {Provider}, secret key {SecretKey}, webhook secret {WebhookSecret}",
            admin, provider ?? "(unchanged)", secretKey is null ? "unchanged" : "replaced", webhookSecret is null ? "unchanged" : "replaced");
        return TypedResults.Ok(Describe(options, providers, cipher));
    }

    /// <summary>
    /// Back to configuration alone: the stored settings, keys included, are deleted.
    /// </summary>
    private static async Task<Results<Ok<PaymentSettingsDto>, ValidationProblem>> ClearAsync(ClaimsPrincipal user, IdentityContext db,
        EffectivePaymentOptions options, PaymentProviders providers, SettingsCipher cipher, ILoggerFactory loggers, CancellationToken ct)
    {
        var refused = await InTransactionAsync(db, async () =>
        {
            // The keys go with the row: configuration alone must be able to carry on selling.
            var before = options.Read(await Locked(db).AsNoTracking().SingleOrDefaultAsync(ct));
            if (ClearRefusal(EffectivePaymentOptions.Merge(options.Configured, before), options.Configured) is { } problem)
            {
                return $"{problem} To stop selling on purpose, choose \"None\" first.";
            }
            await db.PaymentSettings.ExecuteDeleteAsync(ct);
            return null;
        }, ct);
        if (refused is not null)
        {
            return Refused(refused);
        }

        await options.ReloadAsync(db, ct);
        loggers.CreateLogger(typeof(PaymentSettingsApi)).LogInformation("Payment settings cleared by {Admin}; configuration is in force", Admin(user));
        return TypedResults.Ok(Describe(options, providers, cipher));
    }

    /// <summary>
    /// Runs <paramref name="change"/> in a transaction, committed when it returns null (nothing wrong) and rolled back
    /// when it returns what is. Through the execution strategy, as the context's retries require; a retry runs the
    /// whole change again from a clean context.
    /// </summary>
    private static Task<string?> InTransactionAsync(IdentityContext db, Func<Task<string?>> change, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var problem = await change();
            if (problem is null)
            {
                await transaction.CommitAsync(ct);
            }
            return problem;
        });

    private static ValidationProblem Refused(string problem) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["payments"] = [problem] });

    /// <summary>
    /// The one row, locked until the transaction ends.
    /// </summary>
    private static IQueryable<PaymentSettings> Locked(IdentityContext db) => db.PaymentSettings.FromSql($"select * from payment_settings for update");

    /// <summary>
    /// The admin's username, cut to what the column holds (Keycloak allows longer ones).
    /// </summary>
    internal static string? Admin(ClaimsPrincipal user) =>
        user.FindFirstValue("preferred_username") is { } name
            ? name.Length <= PaymentSettings.UpdatedByLength ? name : name[..PaymentSettings.UpdatedByLength]
            : null;

    /// <summary>
    /// Stripe's keys are plain letters, digits and underscores, and not long; anything else isn't one, and could outgrow
    /// its column once encrypted.
    /// </summary>
    private static bool LooksLikeKey(string? key) => key is null || (key.Length <= 255 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

    /// <summary>
    /// Why a change is refused for what it would do to sales, or null. Refused: plans are sold now and wouldn't be
    /// after, though a provider is still chosen; or the change itself chooses a provider that couldn't sell. Not
    /// refused: choosing none, and adding to a provider that isn't selling yet (its keys arrive one at a time).
    /// </summary>
    internal static string? Refusal(PaymentOptions before, PaymentOptions after, bool choosesProvider)
    {
        var missing = PaymentOptionsValidator.Missing(after);
        return missing.Count > 0 && (PaymentOptionsValidator.Sells(before) || choosesProvider)
            ? $"That would stop plans being sold. Still needed: {string.Join(" ", missing)}"
            : null;
    }

    /// <summary>
    /// Why going back to configuration alone is refused, or null: plans sell now and configuration on its own
    /// couldn't carry on, for want of a key or of any provider at all.
    /// </summary>
    internal static string? ClearRefusal(PaymentOptions before, PaymentOptions configured) =>
        Refusal(before, configured, choosesProvider: false) is { } missing ? $"{missing} The deployment's configuration doesn't have it."
        : PaymentOptionsValidator.Sells(before) && configured.Provider is not { Length: > 0 }
            ? "That would stop plans being sold: the deployment's configuration names no provider."
        : null;

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
        : !LooksLikeKey(secretKey) || !LooksLikeKey(webhookSecret)
            ? "That isn't one of Stripe's keys: they are letters, digits and underscores, at most 255 of them."
        : webhookSecret is not null && !webhookSecret.StartsWith("whsec_", StringComparison.Ordinal) ? "Stripe's webhook signing secret starts with whsec_."
        : portal is { Length: > 0 } && !(Uri.TryCreate(portal, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && portal.Length <= PaymentSettings.PortalUrlLength)
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
            providers.Names.Select(name => (Name: name.ToLowerInvariant(), Url: PublicWebhookUrl(current.ReturnUrl, name.ToLowerInvariant())))
                .Where(p => p.Url is not null).ToDictionary(p => p.Name, p => p.Url!),
            cipher.Available,
            stored is not null,
            stored?.UpdatedAtUtc,
            stored?.UpdatedBy,
            stored?.KeysUnreadable == true);
    }

    /// <summary>
    /// Null unless <paramref name="returnUrl"/> is a web address: a path alone (development) says nothing of the site's.
    /// </summary>
    internal static string? PublicWebhookUrl(string? returnUrl, string provider) =>
        Uri.TryCreate(returnUrl, UriKind.Absolute, out var site) && site.Scheme is "http" or "https"
            ? site.GetLeftPart(UriPartial.Authority) + PublicWebhookPath + provider
            : null;

    private static string Source(string? stored, string? configured) =>
        !string.IsNullOrEmpty(stored) ? From.Settings : !string.IsNullOrEmpty(configured) ? From.Configuration : "";

    private static class From
    {
        public const string Settings = "settings";
        public const string Configuration = "configuration";
    }
}

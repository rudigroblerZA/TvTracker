using System.Security.Cryptography;
using System.Text;
using Identity.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Identity.API.Payments;

/// <summary>
/// Encrypts the provider secrets an admin enters (AES-GCM), under a key derived from <c>Payments:SettingsKey</c>: a
/// secret of the deployment's own, so the database alone doesn't give the keys away. Without it nothing secret can be
/// saved from the admin page, and configuration stays the only way to set them.
/// </summary>
public sealed class SettingsCipher(IConfiguration configuration)
{
    private const string Prefix = "v1:";
    private readonly byte[]? key = configuration["Payments:SettingsKey"] is { Length: >= 16 } secret
        ? HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: "tvtracker payment settings"u8.ToArray())
        : null;

    public bool Available => key is not null;

    public string Protect(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(key ?? throw new InvalidOperationException("Payments:SettingsKey isn't set."), tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>
    /// The value, or null when it can't be read: no key, another key than it was saved under, or damaged.
    /// </summary>
    public string? Unprotect(string? stored)
    {
        if (key is null || stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var (nonceSize, tagSize) = (AesGcm.NonceByteSizes.MaxSize, AesGcm.TagByteSizes.MaxSize);
            var plain = new byte[bytes.Length - nonceSize - tagSize];
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(bytes.AsSpan(0, nonceSize), bytes.AsSpan(nonceSize + tagSize), bytes.AsSpan(nonceSize, tagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>
/// What an admin has set for payments, read into plain values.
/// </summary>
/// <param name="Provider">Null follows configuration; "" sells nothing; else a provider's name.</param>
public sealed record StoredPaymentSettings(string? Provider, string? StripeSecretKey, string? StripeWebhookSecret, string? StripePortalUrl);

/// <summary>
/// The payment options in force: configuration (<c>Payments:*</c>), with what an admin set on the Financials page laid
/// over it. It stands in for <see cref="IOptions{PaymentOptions}"/>, so the providers and the checkout read the
/// settings in force without knowing where each came from.
/// <list type="bullet">
/// <item><b>A field an admin left alone</b> keeps its configured value; clearing the settings returns to configuration.</item>
/// <item><b>Read from the database</b> at start, after every save, and every <see cref="RefreshEvery"/>, so another
/// instance of the service follows a change within that time.</item>
/// </list>
/// </summary>
public sealed class EffectivePaymentOptions(IOptionsMonitor<PaymentOptions> configured, SettingsCipher cipher) : IOptions<PaymentOptions>
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    private volatile StoredPaymentSettings? stored;

    public PaymentOptions Configured => configured.CurrentValue;

    public StoredPaymentSettings? Stored => stored;

    public PaymentOptions Value => Merge(configured.CurrentValue, stored);

    public async Task ReloadAsync(IdentityContext db, CancellationToken ct)
    {
        var row = await db.PaymentSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        stored = row is null ? null : new StoredPaymentSettings(row.Provider, cipher.Unprotect(row.StripeSecretKey),
            cipher.Unprotect(row.StripeWebhookSecret), row.StripePortalUrl);
    }

    internal static PaymentOptions Merge(PaymentOptions options, StoredPaymentSettings? settings) => settings is null ? options : new PaymentOptions
    {
        Provider = settings.Provider ?? options.Provider,
        ReturnUrl = options.ReturnUrl,
        GraceDays = options.GraceDays,
        Prices = options.Prices,
        Test = options.Test,
        Stripe = new StripeOptions
        {
            SecretKey = settings.StripeSecretKey ?? options.Stripe.SecretKey,
            WebhookSecret = settings.StripeWebhookSecret ?? options.Stripe.WebhookSecret,
            PortalUrl = settings.StripePortalUrl ?? options.Stripe.PortalUrl,
            ApiBase = options.Stripe.ApiBase
        }
    };
}

/// <summary>
/// Keeps <see cref="EffectivePaymentOptions"/> in step with the database, and says at start what the selling provider
/// still lacks: plans aren't sold until an admin, or configuration, supplies it.
/// </summary>
public sealed class PaymentSettingsRefresher(IServiceScopeFactory scopes, EffectivePaymentOptions options, TimeProvider time,
    ILogger<PaymentSettingsRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await options.ReloadAsync(scope.ServiceProvider.GetRequiredService<IdentityContext>(), stoppingToken);
                if (first && PaymentOptionsValidator.Missing(options.Value) is { Count: > 0 } missing)
                {
                    logger.LogWarning("Plans can't be bought yet: {Missing} Set it in configuration or on the Financials page.", string.Join(" ", missing));
                }
                first = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database may not be ready at start; configuration alone is in force until it is.
                logger.LogDebug(ex, "Couldn't read the payment settings; trying again shortly");
            }
            await Task.Delay(first ? TimeSpan.FromSeconds(3) : EffectivePaymentOptions.RefreshEvery, time, stoppingToken);
        }
    }
}

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
    /// <summary>
    /// The shortest secret taken: a shorter one is a mistake to put right, not a key to encrypt under.
    /// </summary>
    public const int MinSecretLength = 16;

    // No secret at all is a deployment that keeps its keys in configuration. One that's there but too short stops the
    // service at start: quietly doing without would look, on the page, like keys nobody had entered.
    private readonly byte[]? key = configuration["Payments:SettingsKey"] switch
    {
        null or "" => null,
        { Length: < MinSecretLength } => throw new InvalidOperationException($"Payments:SettingsKey must be at least {MinSecretLength} characters."),
        var secret => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: "tvtracker payment settings"u8.ToArray())
    };

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
/// <param name="KeysUnreadable">A key is stored but can't be decrypted: <c>Payments:SettingsKey</c> isn't the one it
/// was saved under. It then counts as not set, and has to be entered again.</param>
public sealed record StoredPaymentSettings(string? Provider, string? StripeSecretKey, string? StripeWebhookSecret, string? StripePortalUrl,
    DateTime? UpdatedAtUtc = null, string? UpdatedBy = null, bool KeysUnreadable = false);

/// <summary>
/// The payment options in force: configuration (<c>Payments:*</c>), with what an admin set on the Financials page laid
/// over it. It stands in for <see cref="IOptions{PaymentOptions}"/>, so the providers and the checkout read the
/// settings in force without knowing where each came from.
/// <list type="bullet">
/// <item><b>A field an admin left alone</b> keeps its configured value; clearing the settings returns to configuration.</item>
/// <item><b>Read from the database</b> at start, after every save, and every <see cref="RefreshEvery"/>, so another
/// instance of the service follows a change within that time.</item>
/// <item><b>Merged once</b> for each pair of configuration and stored settings, not on every read.</item>
/// <item><b>Read before the service takes a request</b> (<see cref="PaymentSettingsRefresher"/> does the first read as
/// it starts): configuration alone could otherwise sell through an account an admin switched off, or refuse webhooks
/// signed with the key they set.</item>
/// </list>
/// </summary>
public sealed class EffectivePaymentOptions(IOptionsMonitor<PaymentOptions> configured, SettingsCipher cipher, ILogger<EffectivePaymentOptions> logger)
    : IOptions<PaymentOptions>
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private volatile StoredPaymentSettings? stored;
    private volatile Merged? merged;

    // One reload at a time, each reading and then publishing: a read begun before a save can't land after the
    // save's own, and put the old settings back.
    private readonly SemaphoreSlim reloading = new(1, 1);

    private sealed record Merged(PaymentOptions From, StoredPaymentSettings? With, PaymentOptions Result);

    public PaymentOptions Configured => configured.CurrentValue;

    public StoredPaymentSettings? Stored => stored;

    public PaymentOptions Value
    {
        get
        {
            var (from, with, last) = (configured.CurrentValue, stored, merged);
            if (last is null || !ReferenceEquals(last.From, from) || !ReferenceEquals(last.With, with))
            {
                merged = last = new Merged(from, with, Merge(from, with));
            }
            return last.Result;
        }
    }

    public async Task ReloadAsync(IdentityContext db, CancellationToken ct)
    {
        await reloading.WaitAsync(ct);
        try
        {
            var (was, now) = (stored, Read(await db.PaymentSettings.AsNoTracking().SingleOrDefaultAsync(ct)));
            stored = now;
            // Said once, when it starts being so: otherwise the only sign is a key that "isn't set".
            if (now is { KeysUnreadable: true } && was is not { KeysUnreadable: true })
            {
                logger.LogWarning("Stripe keys saved on the Financials page can't be decrypted: Payments:SettingsKey isn't the one they were saved "
                    + "under. They count as not set until they are entered again.");
            }
        }
        finally
        {
            reloading.Release();
        }
    }

    /// <summary>
    /// A row as plain values, its keys decrypted; null for no row.
    /// </summary>
    public StoredPaymentSettings? Read(PaymentSettings? row)
    {
        if (row is null)
        {
            return null;
        }
        var (secretKey, webhookSecret) = (cipher.Unprotect(row.StripeSecretKey), cipher.Unprotect(row.StripeWebhookSecret));
        return new StoredPaymentSettings(row.Provider, secretKey, webhookSecret, row.StripePortalUrl, row.UpdatedAtUtc, row.UpdatedBy,
            KeysUnreadable: (row.StripeSecretKey is not null && secretKey is null) || (row.StripeWebhookSecret is not null && webhookSecret is null));
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
        },
        // Polar's settings come from configuration alone: the Financials page chooses it, and doesn't hold its keys.
        Polar = options.Polar
    };
}

/// <summary>
/// Keeps <see cref="EffectivePaymentOptions"/> in step with the database, and says at start what the selling provider
/// still lacks: plans aren't sold until an admin, or configuration, supplies it. The first read is part of starting
/// (the database is up by then: its initializer starts before this), so no request is answered on configuration alone.
/// </summary>
public sealed class PaymentSettingsRefresher(IServiceScopeFactory scopes, EffectivePaymentOptions options, TimeProvider time,
    ILogger<PaymentSettingsRefresher> logger) : BackgroundService
{
    /// <summary>
    /// How soon the first read is tried again when it fails.
    /// </summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Failed reads in a row that are expected while the database starts; after that each is worth a warning.
    /// </summary>
    private const int QuietFailures = 5;

    private int failures;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        while (!await ReloadAsync(cancellationToken))
        {
            await Task.Delay(Retry, time, cancellationToken);
        }
        if (PaymentOptionsValidator.Missing(options.Value) is { Count: > 0 } missing)
        {
            logger.LogWarning("Plans can't be bought yet: {Missing} Set it in configuration or on the Financials page.", string.Join(" ", missing));
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(EffectivePaymentOptions.RefreshEvery, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReloadAsync(stoppingToken);
        }
    }

    private async Task<bool> ReloadAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await options.ReloadAsync(scope.ServiceProvider.GetRequiredService<IdentityContext>(), ct);
            failures = 0;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database may not be ready at start, or briefly away later: what was last read stays in force. A few
            // misses at start are expected; one that lasts is said out loud (and again every twentieth time), since
            // until it's read, what an admin set on the Financials page (Stripe's keys) isn't in force.
            failures++;
            if (failures == QuietFailures + 1 || (failures > QuietFailures && failures % 20 == 0))
            {
                logger.LogWarning(ex, "The payment settings can't be read ({Failures} tries); what an admin set on the Financials page isn't in force", failures);
            }
            else
            {
                logger.LogDebug(ex, "Couldn't read the payment settings; trying again shortly");
            }
            return false;
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TvTracker.Payments;

/// <summary>
/// The test payment provider's webhook signature, Stripe's shape: header <c>TvTracker-Signature:
/// t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}"}</c>. One file, linked into the web app, so the pages that send
/// webhooks and Identity, which checks them, can't disagree about it.
/// </summary>
public static class PaymentSignature
{
    public const string Header = "TvTracker-Signature";

    /// <summary>
    /// How far from now a signature's time may be; outside it a captured webhook can't be replayed.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Hmac(secret, $"{t}.{body}")}";
    }

    /// <summary>
    /// One <c>t</c> and any number of <c>v1</c> values (a provider rotating its secret sends one per secret, as Stripe
    /// does); valid when one matches. Anything malformed is simply not valid.
    /// </summary>
    public static bool Verify(string body, string header, string secret, DateTimeOffset now)
    {
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).Select(p => (Key: p[0].Trim(), Value: p[1].Trim())).ToList();
        var stamps = parts.Where(p => p.Key == "t").ToList();
        if (stamps.Count != 1 || !long.TryParse(stamps[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }
        var expected = Encoding.ASCII.GetBytes(Hmac(secret, $"{stamps[0].Value}.{body}"));
        return parts.Where(p => p.Key == "v1")
            .Any(p => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(p.Value.ToLowerInvariant())));
    }

    private static string Hmac(string secret, string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));
}

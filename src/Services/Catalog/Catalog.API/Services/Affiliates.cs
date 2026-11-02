using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Catalog.API.Services;

/// <summary>The affiliate programs the app has joined (<c>Affiliates:Programs</c>); none by default.</summary>
public sealed class AffiliateOptions
{
    public List<AffiliateProgram> Programs { get; set; } = [];
}

/// <summary>
/// One affiliate program: the services and regions it covers, the link it wraps a service's address in, and the key its
/// network sends with a postback.
/// </summary>
public sealed class AffiliateProgram
{
    /// <summary>Its name in reports and in its postback address (<c>/affiliates/postback/{name}</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Provider names as the catalog lists them ("Netflix"), compared ignoring case.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>ISO 3166-1 codes it pays for; empty means every region.</summary>
    public List<string> Regions { get; set; } = [];

    /// <summary>
    /// The tracked link, https only: <c>{url}</c> is the service's address (escaped) and <c>{click}</c> the click id the
    /// network passes back, e.g. <c>https://network.example/c/123?u={url}&amp;subId1={click}</c>.
    /// </summary>
    public string Template { get; set; } = "";

    /// <summary>The secret its network adds to postbacks (<c>key=</c>); without one, its postbacks are refused.</summary>
    public string? PostbackKey { get; set; }

    /// <summary>ISO 4217 code its commissions are paid in.</summary>
    public string Currency { get; set; } = "USD";
}

/// <summary>
/// Affiliate links: which program covers a click, and the tracked link it becomes. A service with no program gets its
/// plain link, still counted, so the clicks a service would be worth are known before there's a deal.
/// </summary>
public static class AffiliateLinks
{
    /// <summary>The first valid program covering the service in the region, if any.</summary>
    public static AffiliateProgram? For(AffiliateOptions options, string service, string region) =>
        options.Programs.FirstOrDefault(p => IsValid(p)
            && p.Services.Contains(service, StringComparer.OrdinalIgnoreCase)
            && (p.Regions.Count == 0 || p.Regions.Contains(region, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The program's tracked link for <paramref name="url"/> and the click.</summary>
    public static string Wrap(AffiliateProgram program, string url, Guid clickId) =>
        program.Template.Replace("{url}", Uri.EscapeDataString(url)).Replace("{click}", clickId.ToString("N"));

    /// <summary>
    /// A name, a service, a currency code, and a template that gives an absolute https address: anything else would send
    /// people somewhere broken, so an invalid program is skipped (and shown as such in the report).
    /// </summary>
    public static bool IsValid(AffiliateProgram program) =>
        program.Name.Length is > 0 and <= 64 && program.Services.Count > 0 && program.Currency.Length == 3
        && Uri.TryCreate(Wrap(program, "https://example.org/", Guid.Empty), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0;

    /// <summary>Whether a postback's key is the program's, compared in constant time.</summary>
    public static bool KeyMatches(AffiliateProgram program, string? key) =>
        program.PostbackKey is { Length: > 0 } expected && key is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(key));
}

/// <summary>App admins: Keycloak usernames in <c>Admin:Users</c>, as Identity's flags page reads them.</summary>
public static class Admins
{
    public static bool Is(ClaimsPrincipal user, IConfiguration config) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);
}

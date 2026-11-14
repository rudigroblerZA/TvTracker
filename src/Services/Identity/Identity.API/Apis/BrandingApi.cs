using System.Security.Claims;
using System.Text.RegularExpressions;
using Identity.API.Data;
using Identity.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Identity.API.Apis;

/// <summary>
/// What an admin has set in Settings for the app's name, look and the company behind it. Every field is optional: one
/// left empty falls back to the configuration each client and service was started with (<c>Branding:*</c>).
/// </summary>
/// <param name="AppName">The app's name: header, page titles, emails and notification titles.</param>
/// <param name="AccentColor">The colour of links, buttons and the logo tile, as #rrggbb.</param>
/// <param name="Operator">Who runs the app: a company's legal name, shown in the privacy policy and terms.</param>
/// <param name="ContactEmail">Where people write about their data and the terms.</param>
/// <param name="OperatorAddress">The operator's postal address.</param>
/// <param name="CompanyNumber">Its registration, as a phrase: "registered in South Africa, no. 2026/123456/07".</param>
/// <param name="GoverningLaw">Whose law the terms are under: "South Africa".</param>
public record BrandingDto(string? AppName, string? AccentColor, string? Operator, string? ContactEmail, string? OperatorAddress,
    string? CompanyNumber, string? GoverningLaw);

/// <summary>
/// The app's name, accent colour and company details, set by admins in Settings. Reading them needs no sign-in: the
/// signed-out pages (the landing page, the privacy policy, the terms) show them, and the other services ask for the
/// name to put in what they write.
/// </summary>
public static partial class BrandingApi
{
    public static void MapBrandingApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/branding", GetAsync);
        app.MapPut("/api/v1/admin/branding", SaveAsync).RequireAuthorization();
    }

    private static async Task<Ok<BrandingDto>> GetAsync(IdentityContext db, CancellationToken ct) =>
        TypedResults.Ok(ToDto(await db.SiteSettings.AsNoTracking().SingleOrDefaultAsync(ct)));

    private static async Task<Results<Ok<BrandingDto>, NotFound, ValidationProblem>> SaveAsync(BrandingDto request, ClaimsPrincipal user,
        IConfiguration config, IdentityContext db, TimeProvider time, KeycloakAdminClient keycloak, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!FlagsApi.IsAdmin(user, config))
        {
            return TypedResults.NotFound();
        }

        var cleaned = Clean(request);
        if (Problem(cleaned) is { } problem)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["branding"] = [problem] });
        }

        var settings = await db.SiteSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SiteSettings { Id = SiteSettings.OnlyId };
            db.SiteSettings.Add(settings);
        }
        (settings.AppName, settings.AccentColor, settings.Operator, settings.ContactEmail, settings.OperatorAddress, settings.CompanyNumber,
            settings.GoverningLaw, settings.UpdatedAtUtc) = (cleaned.AppName, cleaned.AccentColor, cleaned.Operator, cleaned.ContactEmail,
            cleaned.OperatorAddress, cleaned.CompanyNumber, cleaned.GoverningLaw, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        await RenameMailAsync(MailAccount.AppName(config, settings.AppName), config, keycloak, loggers.CreateLogger(typeof(BrandingApi)), ct);
        return TypedResults.Ok(ToDto(settings));
    }

    /// <summary>
    /// Keycloak's own mail (password reset, confirming an address) comes from the app's name too, so a rename reaches
    /// it. The settings are saved either way: if Keycloak can't be reached now, the next start sets the name.
    /// </summary>
    private static async Task RenameMailAsync(string name, IConfiguration config, KeycloakAdminClient keycloak, ILogger logger, CancellationToken ct)
    {
        if (config["Keycloak:AdminUsername"] is not { Length: > 0 } adminUser || config["Keycloak:AdminPassword"] is not { Length: > 0 } adminPassword)
        {
            return;
        }
        try
        {
            var token = await keycloak.GetMasterAdminTokenAsync(adminUser, adminPassword, ct);
            if (await keycloak.SetMailNameAsync(token, name, ct))
            {
                logger.LogInformation("Keycloak's mail now comes from {Name}", name);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't rename Keycloak's mail; it's set on the next start");
        }
    }

    /// <summary>
    /// Trimmed, with an empty field as none (so it falls back to configuration) and the colour in lower case.
    /// </summary>
    internal static BrandingDto Clean(BrandingDto b)
    {
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new(Text(b.AppName), Text(b.AccentColor)?.ToLowerInvariant(), Text(b.Operator), Text(b.ContactEmail), Text(b.OperatorAddress),
            Text(b.CompanyNumber), Text(b.GoverningLaw));
    }

    /// <summary>
    /// What's wrong with a cleaned set of values, or null. These are shown on public pages, so each is kept short and
    /// plain; the email is only checked for looking like one.
    /// </summary>
    internal static string? Problem(BrandingDto b) =>
        b.AppName is { Length: > 40 } ? "The app's name is at most 40 characters."
        : b.AccentColor is { } colour && !HexColour().IsMatch(colour) ? "The accent colour is six hex digits after a #, like #2563eb."
        : b.Operator is { Length: > 120 } ? "The operator's name is at most 120 characters."
        : b.ContactEmail is { } email && (email.Length > 256 || !Email().IsMatch(email)) ? "The contact address doesn't look like an email address."
        : b.OperatorAddress is { Length: > 300 } ? "The postal address is at most 300 characters."
        : b.CompanyNumber is { Length: > 160 } ? "The company registration is at most 160 characters."
        : b.GoverningLaw is { Length: > 80 } ? "The governing law is at most 80 characters."
        : new[] { b.AppName, b.Operator, b.OperatorAddress, b.CompanyNumber, b.GoverningLaw }.Any(v => v is not null && v.Any(char.IsControl))
            ? "Line breaks and other control characters aren't allowed."
        : null;

    private static BrandingDto ToDto(SiteSettings? s) =>
        s is null ? new(null, null, null, null, null, null, null)
            : new(s.AppName, s.AccentColor, s.Operator, s.ContactEmail, s.OperatorAddress, s.CompanyNumber, s.GoverningLaw);

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();
}

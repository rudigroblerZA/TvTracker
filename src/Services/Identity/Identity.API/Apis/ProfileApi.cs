using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Identity.API.Data;
using Identity.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Apis;

public record ProfileDto(
    string UserId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string TimeZone,
    string Region,
    string Language);

public record UpdateProfileRequest(
    string DisplayName,
    string? AvatarUrl,
    string TimeZone,
    string Region,
    string Language);

public static class ProfileApi
{
    /// <summary>Services that hold per-user data and expose GET api/v1/me/export.</summary>
    private static readonly string[] ExportSources = ["tracking", "ratings", "recommendations", "notifications", "social"];

    public static RouteGroupBuilder MapProfileApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/profile", GetProfileAsync);
        api.MapPut("/profile", UpdateProfileAsync);
        api.MapDelete("/account", DeleteAccountAsync);
        api.MapGet("/account/export", ExportAsync);

        return api;
    }

    /// <summary>Returns the profile, creating it from the token's claims on first sign-in.</summary>
    private static async Task<Ok<ProfileDto>> GetProfileAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(user, db, outbox, ct);
        return TypedResults.Ok(ToDto(profile));
    }

    private static async Task<Results<Ok<ProfileDto>, ValidationProblem>> UpdateProfileAsync(
        UpdateProfileRequest request, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var profile = await GetOrCreateProfileAsync(user, db, outbox, ct);

        profile.DisplayName = request.DisplayName.Trim();
        profile.AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        profile.TimeZone = request.TimeZone;
        profile.Region = request.Region.ToUpperInvariant();
        profile.Language = request.Language;
        profile.Email = user.GetEmail() ?? profile.Email;
        profile.UpdatedAtUtc = DateTime.UtcNow;

        outbox.Add(new UserProfileUpdatedIntegrationEvent(
            profile.UserId, profile.DisplayName, profile.Email, profile.TimeZone, profile.Region, profile.Language));

        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        return TypedResults.Ok(ToDto(profile));
    }

    /// <summary>
    /// Deletes the Keycloak user and the profile, then publishes UserDeleted so every service purges the user's data.
    /// </summary>
    private static async Task<NoContent> DeleteAccountAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, KeycloakAdminClient keycloak, CancellationToken ct)
    {
        var userId = user.GetUserId();

        await keycloak.DeleteUserAsync(userId, ct);

        var profile = await db.Profiles.FindAsync([userId], ct);
        if (profile is not null)
        {
            db.Profiles.Remove(profile);
        }

        outbox.Add(new UserDeletedIntegrationEvent(userId));

        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Collects the user's data from every service into one JSON document.
    /// The caller's access token is forwarded to each service.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpContext httpContext, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox,
        IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Identity.API.Export");
        var profile = await GetOrCreateProfileAsync(user, db, outbox, ct);
        var authorization = httpContext.Request.Headers.Authorization.ToString();

        var export = new JsonObject
        {
            ["exportedAtUtc"] = DateTime.UtcNow,
            ["profile"] = JsonSerializer.SerializeToNode(ToDto(profile))
        };

        foreach (var source in ExportSources)
        {
            try
            {
                var client = httpClientFactory.CreateClient(source);
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/me/export");
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                export[source] = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                logger.LogWarning(ex, "Export from {Source} failed", source);
                export[source] = new JsonObject { ["error"] = "This service's data could not be exported right now." };
            }
        }

        return Results.Text(export.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json");
    }

    private static async Task<UserProfile> GetOrCreateProfileAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var profile = await db.Profiles.FindAsync([userId], ct);
        if (profile is not null)
        {
            return profile;
        }

        var now = DateTime.UtcNow;
        profile = new UserProfile
        {
            UserId = userId,
            DisplayName = user.GetDisplayName() ?? "New user",
            Email = user.GetEmail(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Profiles.Add(profile);
        outbox.Add(new UserRegisteredIntegrationEvent(profile.UserId, profile.DisplayName, profile.Email));

        try
        {
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request created it at the same time.
            db.ChangeTracker.Clear();
            profile = await db.Profiles.SingleAsync(p => p.UserId == userId, ct);
        }

        return profile;
    }

    private static Dictionary<string, string[]> Validate(UpdateProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 100)
        {
            errors["displayName"] = ["Display name is required and must be at most 100 characters."];
        }
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out _))
        {
            errors["timeZone"] = ["Unknown timezone. Use an IANA id such as 'Africa/Johannesburg'."];
        }
        if (request.Region is not { Length: 2 } || !request.Region.All(char.IsLetter))
        {
            errors["region"] = ["Region must be a two-letter country code."];
        }
        if (string.IsNullOrWhiteSpace(request.Language) || request.Language.Length > 10)
        {
            errors["language"] = ["Language is required."];
        }
        if (request.AvatarUrl is { Length: > 0 } url && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            errors["avatarUrl"] = ["Avatar must be an absolute URL."];
        }

        return errors;
    }

    private static ProfileDto ToDto(UserProfile p) =>
        new(p.UserId, p.DisplayName, p.Email, p.AvatarUrl, p.TimeZone, p.Region, p.Language);
}

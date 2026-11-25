using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Identity.API.Services;
using Identity.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Apis;

/// <param name="Avatar">The picture to show: the uploaded one's path under api/v1/, else <paramref name="AvatarUrl"/>, else null.</param>
/// <param name="AvatarUploaded">Whether <paramref name="Avatar"/> is one they uploaded.</param>
public record ProfileDto(
    string UserId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string TimeZone,
    string Region,
    string Language,
    string? Avatar = null,
    bool AvatarUploaded = false);

/// <param name="Image">The picture's bytes (base64 in JSON).</param>
public record UploadAvatarRequest(byte[]? Image);

public record UpdateProfileRequest(
    string DisplayName,
    string? AvatarUrl,
    string TimeZone,
    string Region,
    string Language);

public static class ProfileApi
{
    /// <summary>
    /// Services that hold per-user data and expose GET api/v1/me/export; each is an HttpClient of that name.
    /// </summary>
    internal static readonly string[] ExportSources = ["tracking", "ratings", "recommendations", "notifications", "social", "analytics", "research", "financials"];

    public static RouteGroupBuilder MapProfileApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/profile", GetProfileAsync);
        api.MapPut("/profile", UpdateProfileAsync);
        // The picture (JPEG, PNG or WebP, already shrunk by the client) as base64 in JSON, like every other request
        // here. The body may be a third bigger than the picture, and little more.
        api.MapPut("/me/avatar", UploadAvatarAsync).WithMetadata(new RequestSizeLimitAttribute(ProfileAvatar.MaxBytes * 3 / 2));
        api.MapDelete("/me/avatar", RemoveAvatarAsync);
        api.MapDelete("/account", DeleteAccountAsync);
        // Each export reads every service, so a person gets a few an hour.
        api.MapGet("/account/export", ExportAsync).RequireRateLimiting(ClientRateLimiting.Export);

        // Anyone may see a picture: profiles, people lists and the feed show them, signed in or not.
        app.MapGet("/api/v1/avatars/{userId}", GetAvatarAsync).AllowAnonymous();

        return api;
    }

    /// <summary>
    /// Keeps the picture sent as the caller's own, replacing any they had. It is taken as sent: the client shrinks
    /// it first, and here only its kind (from its first bytes, not from anything the request claims) and weight are checked.
    /// </summary>
    private static async Task<Results<Ok<ProfileDto>, ValidationProblem>> UploadAvatarAsync(
        UploadAvatarRequest request, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var bytes = request.Image ?? [];
        var contentType = ProfileAvatar.ContentTypeOf(bytes);
        if (bytes.Length > ProfileAvatar.MaxBytes || contentType is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["image"] = [bytes.Length > ProfileAvatar.MaxBytes
                    ? $"That picture is too big. It can be up to {ProfileAvatar.MaxBytes / 1024} KB."
                    : "That isn't a picture this can show. Use a JPEG, PNG or WebP."]
            });
        }

        var profile = await GetOrCreateProfileAsync(user, db, outbox, time, ct);
        var now = time.GetUtcNow();
        if (await db.Avatars.FindAsync([profile.UserId], ct) is { } avatar)
        {
            (avatar.ContentType, avatar.Bytes, avatar.UpdatedAtUtc) = (contentType, bytes, now.UtcDateTime);
        }
        else
        {
            db.Avatars.Add(new ProfileAvatar { UserId = profile.UserId, ContentType = contentType, Bytes = bytes, UpdatedAtUtc = now.UtcDateTime });
        }
        profile.AvatarVersion = now.ToUnixTimeMilliseconds();
        return TypedResults.Ok(await SaveProfileAsync(profile, db, outbox, now, ct));
    }

    /// <summary>
    /// Takes the caller's uploaded picture away; their picture is then the one at their avatar address, if they gave one.
    /// </summary>
    private static async Task<Ok<ProfileDto>> RemoveAvatarAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(user, db, outbox, time, ct);
        if (profile.AvatarVersion is null)
        {
            return TypedResults.Ok(ToDto(profile));
        }
        if (await db.Avatars.FindAsync([profile.UserId], ct) is { } avatar)
        {
            db.Avatars.Remove(avatar);
        }
        profile.AvatarVersion = null;
        return TypedResults.Ok(await SaveProfileAsync(profile, db, outbox, time.GetUtcNow(), ct));
    }

    /// <summary>
    /// A user's uploaded picture. Its address changes with every upload (the v a profile's <c>Avatar</c> carries), so
    /// it may be kept for good; it is sent as the kind found when it was uploaded, to be shown and never run.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, NotFound>> GetAvatarAsync(string userId, HttpResponse response, IdentityContext db, CancellationToken ct)
    {
        var avatar = await db.Avatars.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId, ct);
        if (avatar is null)
        {
            return TypedResults.NotFound();
        }
        response.Headers.CacheControl = "public, max-age=31536000, immutable";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        // With its upload time, so a browser that asks again about a copy it holds is told it's still good (304).
        return TypedResults.File(avatar.Bytes, avatar.ContentType, lastModified: new DateTimeOffset(avatar.UpdatedAtUtc, TimeSpan.Zero));
    }

    /// <summary>
    /// Saves a changed profile and tells the other services, which show its name and picture.
    /// </summary>
    private static async Task<ProfileDto> SaveProfileAsync(UserProfile profile, IdentityContext db, IIntegrationEventOutbox outbox, DateTimeOffset now,
        CancellationToken ct)
    {
        profile.UpdatedAtUtc = now.UtcDateTime;
        outbox.Add(new UserProfileUpdatedIntegrationEvent(
            profile.UserId, profile.DisplayName, profile.Email, profile.TimeZone, profile.Region, profile.Language, profile.Avatar));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return ToDto(profile);
    }

    /// <summary>
    /// Returns the profile, creating it from the token's claims on first sign-in.
    /// </summary>
    private static async Task<Ok<ProfileDto>> GetProfileAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var profile = await GetOrCreateProfileAsync(user, db, outbox, time, ct);
        return TypedResults.Ok(ToDto(profile));
    }

    private static async Task<Results<Ok<ProfileDto>, ValidationProblem>> UpdateProfileAsync(
        UpdateProfileRequest request, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var profile = await GetOrCreateProfileAsync(user, db, outbox, time, ct);

        profile.DisplayName = request.DisplayName.Trim();
        profile.AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        profile.TimeZone = request.TimeZone;
        profile.Region = request.Region.ToUpperInvariant();
        profile.Language = request.Language;
        profile.Email = user.GetEmail() ?? profile.Email;

        return TypedResults.Ok(await SaveProfileAsync(profile, db, outbox, time.GetUtcNow(), ct));
    }

    /// <summary>
    /// Deletes the profile and then the Keycloak user, and publishes UserDeleted so every service purges the user's data
    /// (Financials.API stops their subscription at the payment provider when it hears).
    /// An owner's family ends with them (its members go back to their own plans); a member just leaves theirs.
    /// </summary>
    private static async Task<NoContent> DeleteAccountAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, KeycloakAdminClient keycloak, TimeProvider time,
        CancellationToken ct)
    {
        await DeleteAccountByIdAsync(user.GetUserId(), db, outbox, keycloak, time, ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The account deletion itself, for the endpoint and for <see cref="Services.DemoAccountRemover"/>.
    /// </summary>
    internal static async Task DeleteAccountByIdAsync(string userId, IdentityContext db, IIntegrationEventOutbox outbox, KeycloakAdminClient keycloak,
        TimeProvider time, CancellationToken ct)
    {
        var profile = await db.Profiles.FindAsync([userId], ct);
        if (profile is not null)
        {
            db.Profiles.Remove(profile);
        }
        await db.Avatars.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);

        db.FamilyMembers.RemoveRange(await db.FamilyMembers.Where(m => m.UserId == userId).ToListAsync(ct));
        var members = await db.FamilyMembers.Where(m => m.OwnerUserId == userId).ToListAsync(ct);
        db.FamilyMembers.RemoveRange(members);
        db.Families.RemoveRange(await db.Families.Where(f => f.OwnerUserId == userId).ToListAsync(ct));
        await PlanState.RecomputeAsync(db, outbox, time.GetUtcNow(), members.Select(m => m.UserId).ToList(), ct);

        outbox.Add(new UserDeletedIntegrationEvent(userId));

        await db.SaveChangesAsync(ct);
        // The login goes last: were it to go first and the save then fail, the person's data would stay with no way
        // for them to sign in and ask again. This way round, a failure here leaves a login they can delete once more.
        await keycloak.DeleteUserAsync(userId, ct);
        await outbox.PublishAddedAsync(ct);
    }

    /// <summary>
    /// Collects the user's data from every service into one JSON document.
    /// The caller's access token is forwarded to each service.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpContext httpContext, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox,
        IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, TimeProvider time, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Identity.API.Export");
        var profile = await GetOrCreateProfileAsync(user, db, outbox, time, ct);
        var authorization = httpContext.Request.Headers.Authorization.ToString();

        var export = new JsonObject
        {
            ["exportedAtUtc"] = time.GetUtcNow().UtcDateTime,
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

    internal static async Task<UserProfile> GetOrCreateProfileAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var profile = await db.Profiles.FindAsync([userId], ct);
        if (profile is not null)
        {
            return profile;
        }

        var now = time.GetUtcNow().UtcDateTime;
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
        // https only: a page served over https won't show a picture fetched over http.
        if (request.AvatarUrl is { Length: > 0 } url
            && (url.Length > 1024 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps))
        {
            errors["avatarUrl"] = ["The picture's address must start with https://."];
        }

        return errors;
    }

    private static ProfileDto ToDto(UserProfile p) =>
        new(p.UserId, p.DisplayName, p.Email, p.AvatarUrl, p.TimeZone, p.Region, p.Language, p.Avatar, p.AvatarVersion is not null);
}

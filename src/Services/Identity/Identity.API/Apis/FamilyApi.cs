using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Identity.API.Data;
using Identity.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Apis;

/// <param name="Role">"owner", "member" or "none".</param>
/// <param name="Active">Whether the owner pays for the family plan now; members have it only then.</param>
/// <param name="Seats">How many people the owner can invite (<see cref="Plans.FamilyMembers"/>).</param>
/// <param name="InviteToken">The invite link's secret, for the owner only; null while invites are off.</param>
public record FamilyDto(string Role, bool Active, string? OwnerName, IReadOnlyList<FamilyMemberDto> Members, int Seats, string? InviteToken);

/// <param name="UserId">For the owner, to remove them; null for members, who see only names.</param>
public record FamilyMemberDto(string? UserId, string DisplayName, DateTime JoinedAtUtc);

/// <summary>What an invite link shows before joining.</summary>
public record FamilyInviteDto(string OwnerName, int SeatsLeft, bool Active);

public record JoinFamilyRequest(string Token);

public record FamilyInviteLinkDto(string Token);

/// <summary>
/// The family plan (<see cref="Plans.Family"/>): one subscription, shared. Its owner invites up to
/// <see cref="Plans.FamilyMembers"/> people by link; each member's effective plan is "family", with Plus's limits,
/// for as long as the owner pays for it (<see cref="PlanState"/>). Behind the <c>family-plan</c> flag.
/// <list type="bullet">
/// <item>A person is in one family at most, and an owner of a family with members can't join another.</item>
/// <item>Leaving, being removed, or the owner stopping the plan or deleting their account takes the family plan away
/// at once; anything they made on it stays, as with any downgrade.</item>
/// <item>The invite link is 128 random bits, like a list's link; turning invites off clears it.</item>
/// </list>
/// </summary>
public static class FamilyApi
{
    public static void MapFamilyApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireFeature(FeatureFlags.FamilyPlan);

        api.MapGet("/me/family", GetMineAsync);
        api.MapPost("/me/family/invite", CreateInviteAsync);
        api.MapDelete("/me/family/invite", DeleteInviteAsync);
        api.MapDelete("/me/family/members/{userId}", RemoveMemberAsync);
        api.MapDelete("/me/family/membership", LeaveAsync);
        api.MapGet("/family/invites/{token}", GetInviteAsync);
        api.MapPost("/family/join", JoinAsync);
    }

    private static async Task<Ok<FamilyDto>> GetMineAsync(ClaimsPrincipal user, IdentityContext db, CancellationToken ct) =>
        TypedResults.Ok(await FamilyOfAsync(db, user.GetUserId(), ct));

    /// <summary>Turns invites on for the owner of a family plan, or returns the link they have.</summary>
    private static async Task<Results<Ok<FamilyInviteLinkDto>, ProblemHttpResult>> CreateInviteAsync(
        ClaimsPrincipal user, IdentityContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.Profiles.FindAsync([userId], ct) is not { Plan: Plans.Family })
        {
            return TypedResults.Problem($"Inviting people is part of the {Plans.Name(Plans.Family)} plan.", statusCode: StatusCodes.Status403Forbidden,
                title: "Not on your plan", type: "plan-limit");
        }

        var family = await db.Families.FindAsync([userId], ct);
        if (family is null)
        {
            family = new Family { OwnerUserId = userId, CreatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.Families.Add(family);
        }
        family.InviteToken ??= Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new FamilyInviteLinkDto(family.InviteToken));
    }

    /// <summary>Stops the invite link working; members stay.</summary>
    private static async Task<NoContent> DeleteInviteAsync(ClaimsPrincipal user, IdentityContext db, CancellationToken ct)
    {
        if (await db.Families.FindAsync([user.GetUserId()], ct) is { } family)
        {
            family.InviteToken = null;
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveMemberAsync(
        string userId, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var ownerId = user.GetUserId();
        if (await db.FamilyMembers.FindAsync([userId], ct) is not { } member || member.OwnerUserId != ownerId)
        {
            return TypedResults.NotFound();
        }
        db.FamilyMembers.Remove(member);
        await PlanState.RecomputeAsync(db, outbox, time.GetUtcNow(), [userId], ct);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> LeaveAsync(
        ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.FamilyMembers.FindAsync([userId], ct) is not { } member)
        {
            return TypedResults.NotFound();
        }
        db.FamilyMembers.Remove(member);
        await PlanState.RecomputeAsync(db, outbox, time.GetUtcNow(), [userId], ct);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Who invited you and whether there's room, for the page an invite link opens.</summary>
    private static async Task<Results<Ok<FamilyInviteDto>, NotFound>> GetInviteAsync(string token, IdentityContext db, CancellationToken ct)
    {
        if (await FindByTokenAsync(db, token, ct) is not { } found)
        {
            return TypedResults.NotFound();
        }
        var members = await db.FamilyMembers.CountAsync(m => m.OwnerUserId == found.Owner.UserId, ct);
        return TypedResults.Ok(new FamilyInviteDto(found.Owner.DisplayName, Math.Max(0, Plans.FamilyMembers - members),
            found.Owner.Plan == Plans.Family));
    }

    private static async Task<Results<Ok<FamilyDto>, NotFound, ProblemHttpResult>> JoinAsync(
        JoinFamilyRequest request, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindByTokenAsync(db, request.Token, ct) is not { } found)
        {
            return TypedResults.NotFound();
        }
        var ownerId = found.Owner.UserId;
        // Make sure the joiner has a profile, so their effective plan can be kept.
        await ProfileApi.GetOrCreateProfileAsync(user, db, outbox, ct);

        if (await db.FamilyMembers.FindAsync([userId], ct) is { } existing)
        {
            return existing.OwnerUserId == ownerId
                ? TypedResults.Ok(await FamilyOfAsync(db, userId, ct))
                : Conflict("You're already in another family. Leave it first, from your plan page.");
        }
        if (ownerId == userId)
        {
            return Conflict("This is your own family's invite link. Send it to the people you want to invite.");
        }
        if (found.Owner.Plan != Plans.Family)
        {
            return Conflict($"{found.Owner.DisplayName} isn't on the {Plans.Name(Plans.Family)} plan any more.");
        }
        if (await db.FamilyMembers.AnyAsync(m => m.OwnerUserId == userId, ct))
        {
            return Conflict("You run a family of your own, so you can't join another.");
        }
        if (await db.FamilyMembers.CountAsync(m => m.OwnerUserId == ownerId, ct) >= Plans.FamilyMembers)
        {
            return Conflict($"This family is full: it has {Plans.FamilyMembers} members.");
        }

        var now = time.GetUtcNow();
        db.FamilyMembers.Add(new FamilyMember { UserId = userId, OwnerUserId = ownerId, JoinedAtUtc = now.UtcDateTime });
        await PlanState.RecomputeAsync(db, outbox, now, [userId], ct);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return TypedResults.Ok(await FamilyOfAsync(db, userId, ct));
    }

    private static async Task<(Family Family, UserProfile Owner)?> FindByTokenAsync(IdentityContext db, string token, CancellationToken ct)
    {
        if (token.Length is 0 or > 32 || await db.Families.AsNoTracking().FirstOrDefaultAsync(f => f.InviteToken == token, ct) is not { } family
            || await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == family.OwnerUserId, ct) is not { } owner)
        {
            return null;
        }
        return (family, owner);
    }

    private static async Task<FamilyDto> FamilyOfAsync(IdentityContext db, string userId, CancellationToken ct)
    {
        var membership = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == userId, ct);
        var ownerId = membership?.OwnerUserId ?? userId;
        var family = await db.Families.AsNoTracking().FirstOrDefaultAsync(f => f.OwnerUserId == ownerId, ct);
        var owner = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == ownerId, ct);
        var isOwner = membership is null;
        if (isOwner && owner?.Plan != Plans.Family && family is null)
        {
            return new FamilyDto("none", false, null, [], Plans.FamilyMembers, null);
        }

        var members = await db.FamilyMembers.AsNoTracking().Where(m => m.OwnerUserId == ownerId)
            .Join(db.Profiles, m => m.UserId, p => p.UserId, (m, p) => new { m.UserId, p.DisplayName, m.JoinedAtUtc })
            .OrderBy(m => m.JoinedAtUtc)
            .ToListAsync(ct);
        return new FamilyDto(
            isOwner ? "owner" : "member",
            owner?.Plan == Plans.Family,
            owner?.DisplayName,
            members.Select(m => new FamilyMemberDto(isOwner ? m.UserId : null, m.DisplayName, m.JoinedAtUtc)).ToList(),
            Plans.FamilyMembers,
            isOwner ? family?.InviteToken : null);
    }

    private static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Can't join this family");
}

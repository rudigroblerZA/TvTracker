using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.Data;

namespace Social.API.Apis;

public record BlockedBetweenDto(bool Blocked);

/// <summary>
/// For other services only: whether either of two members has blocked the other. Analytics asks before it lets one
/// member answer another's Taste Match, since only Social knows who has blocked whom. Says nothing of who blocked whom.
/// </summary>
public static class BlocksInternalApi
{
    public static void MapBlocksInternalApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/internal/blocks/between", BetweenAsync).RequireAuthorization(AuthenticationExtensions.InternalPolicy);

    private static async Task<Ok<BlockedBetweenDto>> BetweenAsync(string a, string b, SocialContext db, CancellationToken ct) =>
        TypedResults.Ok(new BlockedBetweenDto(await db.Blocks.AsNoTracking()
            .AnyAsync(x => (x.BlockerId == a && x.BlockedId == b) || (x.BlockerId == b && x.BlockedId == a), ct)));
}

using Microsoft.EntityFrameworkCore;
using Social.API.Data;

namespace Social.API.Apis;

/// <summary>
/// Who can see whom, in one place, so the feed, pages, lists, the show page and "watching together" all agree.
/// <list type="bullet">
/// <item>A block, either way round, hides the two from each other everywhere.</item>
/// <item>A private profile shows nothing beyond its name to anyone but approved followers.</item>
/// <item>Activity (what they watch, follow and rate) also needs the owner's "share my activity" on.</item>
/// </list>
/// </summary>
internal static class Visibility
{
    /// <summary>The people <paramref name="me"/> has blocked or been blocked by.</summary>
    public static IQueryable<string> BlockedWith(SocialContext db, string me) =>
        db.Blocks.Where(b => b.BlockerId == me).Select(b => b.BlockedId)
            .Union(db.Blocks.Where(b => b.BlockedId == me).Select(b => b.BlockerId));

    /// <summary>The people <paramref name="me"/> follows (approved) whose activity they can see: the feed's authors.</summary>
    public static IQueryable<string> VisibleFollowees(SocialContext db, string me)
    {
        var blocked = BlockedWith(db, me);
        return db.Follows
            .Where(f => f.FollowerId == me && f.Approved && !blocked.Contains(f.FolloweeId))
            .Select(f => f.FolloweeId)
            .Where(id => db.Profiles.Any(p => p.UserId == id && p.ShareActivity));
    }

    /// <param name="Profile">Their follower counts, lists and the like; for a private profile, only approved followers.</param>
    /// <param name="Activity">What they watch, follow and rate.</param>
    public sealed record Access(bool Exists, bool Profile, bool Activity);

    public static async Task<Access> AccessAsync(SocialContext db, string me, Profile? owner, CancellationToken ct)
    {
        if (owner is null || await BlockedWith(db, me).ContainsAsync(owner.UserId, ct))
        {
            return new Access(false, false, false);
        }
        if (owner.UserId == me)
        {
            return new Access(true, true, true);
        }

        var profile = !owner.Private || await db.Follows.AnyAsync(f => f.FollowerId == me && f.FolloweeId == owner.UserId && f.Approved, ct);
        return new Access(true, profile, profile && owner.ShareActivity);
    }
}

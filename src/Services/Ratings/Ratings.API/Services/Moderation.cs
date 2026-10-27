using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ratings.API.Data;

namespace Ratings.API.Services;

public enum TextVerdict { Ok, Held, Rejected }

/// <summary>
/// The light moderation layer: who moderates, what a post may contain, how often people may post and flag, and what
/// flags do on their own before a moderator looks.
/// <list type="bullet">
/// <item><b>Moderators:</b> the usernames in <c>Admin:Users</c> and <c>Moderation:Users</c>.</item>
/// <item><b>Blocked terms</b> (<c>Moderation:BlockedTerms</c>, whole words) are refused when posting.</item>
/// <item><b>Links</b> are held for review, the usual shape of spam; the author sees the post meanwhile.</item>
/// <item>
///   <b>Flags:</b> <see cref="HideAfterFlags"/> people flagging hides a post until a moderator looks;
///   <see cref="SpoilerAfterFlags"/> flagging it as a spoiler marks it a spoiler instead, which is usually all it needs.
/// </item>
/// <item><b>Rates:</b> per person per hour, so one account can't flood a thread or flag everything.</item>
/// </list>
/// </summary>
public sealed partial class Moderation(IConfiguration config)
{
    public const int HideAfterFlags = 3;
    public const int SpoilerAfterFlags = 2;
    public const int PostsPerHour = 20;
    public const int FlagsPerHour = 30;

    public static readonly IReadOnlySet<string> FlagReasons = new HashSet<string> { "spoiler", "abuse", "spam", "off-topic", "wrong" };

    public bool IsModerator(ClaimsPrincipal user) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Concat(config.GetSection("Moderation:Users").Get<string[]>() ?? [])
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    public TextVerdict Check(string text)
    {
        var blocked = config.GetSection("Moderation:BlockedTerms").Get<string[]>() ?? [];
        if (blocked.Any(term => term.Length > 0 && Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase)))
        {
            return TextVerdict.Rejected;
        }
        return Link().IsMatch(text) ? TextVerdict.Held : TextVerdict.Ok;
    }

    /// <summary>Comments, insights and corrections the user posted in the last hour reached <see cref="PostsPerHour"/>.</summary>
    public static async Task<bool> TooManyPostsAsync(RatingsContext db, string userId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-1);
        var posts = await db.Comments.CountAsync(c => c.UserId == userId && c.CreatedAtUtc > since, ct)
            + await db.Insights.CountAsync(i => i.UserId == userId && i.CreatedAtUtc > since, ct)
            + await db.Corrections.CountAsync(c => c.UserId == userId && c.CreatedAtUtc > since, ct);
        return posts >= PostsPerHour;
    }

    public static async Task<bool> TooManyFlagsAsync(RatingsContext db, string userId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-1);
        return await db.Flags.CountAsync(f => f.UserId == userId && f.CreatedAtUtc > since, ct) >= FlagsPerHour;
    }

    [GeneratedRegex(@"https?://|www\.\S", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}

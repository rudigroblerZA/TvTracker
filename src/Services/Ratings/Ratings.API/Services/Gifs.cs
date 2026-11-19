using System.Text.RegularExpressions;

namespace Ratings.API.Services;

/// <summary>
/// GIFs in comments, from GIPHY (<c>Giphy</c>). Off without a key: nobody can post one, and the ones already posted
/// aren't sent to readers, so clearing the key takes every GIF off the site at once.
/// <list type="bullet">
/// <item><b>The key is a browser key.</b> GIPHY's terms ask for its API to be called from the client, not through a
/// server, so the key is handed to signed-in readers and their browser does the searching and the showing.</item>
/// <item><b>Only ids are stored.</b> The same terms forbid keeping GIPHY's media or their addresses.</item>
/// </list>
/// </summary>
public sealed class GiphyOptions
{
    public string? ApiKey { get; set; }

    /// <summary>
    /// The strongest content rating searches return: "g", "pg", "pg-13" or "r".
    /// </summary>
    public string Rating { get; set; } = "pg";
}

public static partial class Gifs
{
    public static readonly IReadOnlySet<string> Ratings = new HashSet<string> { "g", "pg", "pg-13", "r" };

    public static bool Enabled(GiphyOptions options) => !string.IsNullOrWhiteSpace(options.ApiKey);

    /// <summary>
    /// The configured rating, or "pg" when it isn't one GIPHY knows.
    /// </summary>
    public static string RatingOf(GiphyOptions options) =>
        options.Rating?.Trim().ToLowerInvariant() is { } rating && Ratings.Contains(rating) ? rating : "pg";

    /// <summary>
    /// Whether <paramref name="id"/> has the shape of a GIPHY id: letters and digits only. It goes into a request to
    /// GIPHY from every reader's browser, so nothing else is let through.
    /// </summary>
    public static bool IsId(string? id) => id is not null && Id().IsMatch(id);

    /// <summary>
    /// A comment as a moderator reads it: its words, and its GIF as the address of its page on giphy.com.
    /// </summary>
    public static string WithGif(string text, string? gifId) =>
        gifId is null ? text : $"{text} [GIF: https://giphy.com/gifs/{gifId}]".TrimStart();

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$")]
    private static partial Regex Id();
}

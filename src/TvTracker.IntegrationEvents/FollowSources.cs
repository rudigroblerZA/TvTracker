namespace TvTracker.IntegrationEvents;

// Shared by the UI (which links this file and says where a show page was opened from) and Tracking (which counts
// follows by it), so both use the same names.

/// <summary>
/// Where a follow started: the tool whose result the member opened the show from. Carried as <c>?from=</c> on the
/// link to the show page and on the follow request, and counted by Tracking (the <c>follows.started</c> metric).
/// Nothing about the member is kept: it is a count per source.
/// </summary>
public static class FollowSources
{
    public const string Blend = "blend";
    public const string Mixtape = "mixtape";
    public const string Match = "match";

    /// <summary>
    /// A follow that started anywhere else: search, a list, the show's own address.
    /// </summary>
    public const string Direct = "direct";

    /// <summary>
    /// The query parameter the source travels in.
    /// </summary>
    public const string Parameter = "from";

    private static readonly HashSet<string> Known = [Blend, Mixtape, Match];

    /// <summary>
    /// The source to count a follow under: <paramref name="from"/> when it names a known one, <see cref="Direct"/>
    /// otherwise. The address is the caller's to write, so anything unknown is not a source of its own.
    /// </summary>
    public static string Of(string? from) => from?.Trim().ToLowerInvariant() is { } source && Known.Contains(source) ? source : Direct;
}

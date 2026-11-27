namespace TvTracker.InsightsEngine;

/// <param name="Score">How well the two tastes match, <see cref="TasteMatch.MinScore"/> to <see cref="TasteMatch.MaxScore"/>.</param>
/// <param name="Line">One sentence for the score's band; the same every time for the same match.</param>
/// <param name="BothLove">Shows both love, the most loved first.</param>
/// <param name="BothInto">Genres strong on both sides.</param>
/// <param name="Differ">What each brings that the other doesn't; null when their genres are too alike to say.</param>
/// <param name="WatchTogether">Shows neither knows that sit between the two tastes; empty when too few were found.</param>
/// <param name="Loose">The two have little in common, so <paramref name="WatchTogether"/> is the middle ground, not close matches.</param>
public sealed record TasteMatchDto(int Score, string Line, IReadOnlyList<MatchShowDto> BothLove, IReadOnlyList<string> BothInto,
    MatchDifferenceDto? Differ, IReadOnlyList<MatchPickDto> WatchTogether, bool Loose);

public sealed record MatchShowDto(Guid ShowId, string Title, string? PosterUrl);

/// <param name="Inviter">A genre the inviter watches far more of.</param>
/// <param name="Friend">A genre the friend watches far more of.</param>
public sealed record MatchDifferenceDto(string Inviter, string Friend);

/// <param name="FromInviter">Genres it shares with the inviter's side only.</param>
/// <param name="FromFriend">Genres it shares with the friend's side only.</param>
/// <param name="FromBoth">Genres both sides have.</param>
public sealed record MatchPickDto(Guid ShowId, string Title, string? PosterUrl, IReadOnlyList<string> FromInviter, IReadOnlyList<string> FromFriend,
    IReadOnlyList<string> FromBoth);

/// <summary>
/// Taste Match: how well two people's TV tastes match, what they share, where they differ, and what they could watch
/// together. A side is the shows someone loves, each with a weight: a member's from their ratings, follows and watches
/// (<see cref="Reunions.LoveWeight"/>), a guest's from the shows they picked (weight 1).
/// <list type="bullet">
/// <item><b>Score:</b> three parts, each 0-1. <i>Alike</i> (how close the two sets are in meaning; worked out by the
/// catalog, which has the vectors), <i>kinds</i> (the cosine of the two sides' genre shares) and <i>shared</i> (shows
/// both love). Every part is a share or an average, never a count, so three picks against three hundred loved shows
/// still gives a sensible number.</item>
/// <item><b>Bounds:</b> never under <see cref="MinScore"/> (thin input shouldn't read as an insult) or over
/// <see cref="MaxScore"/> (nobody is a perfect match).</item>
/// <item><b>What may be named:</b> shows both love, genres, and the suggestions. Never one side's shows wholesale, and
/// never a show its owner asked not to have named (it still counts towards the score).</item>
/// <item><b>Order doesn't matter:</b> the score, the shared shows and the genres are the same whichever side is first;
/// only <see cref="MatchDifferenceDto"/> and the picks' reasons say who is who.</item>
/// </list>
/// The weights, the curve and the bands were set on local test members and want checking on real pairs.
/// </summary>
public static class TasteMatch
{
    /// <summary>
    /// Loved shows a member needs before they can start a match.
    /// </summary>
    public const int MinLoved = 5;

    public const int MinPicks = 3;
    public const int MaxPicks = 8;

    /// <summary>
    /// A side's heaviest shows that are scored; a longer history is cut to these.
    /// </summary>
    public const int MaxScored = 50;

    /// <summary>
    /// Shows of a member's side that a result could name, shown to them before their side is used.
    /// </summary>
    public const int MaxNameable = 8;

    public const int MinScore = 15;
    public const int MaxScore = 99;

    public const double AlikeWeight = 0.5;
    public const double KindsWeight = 0.3;
    public const double SharedWeight = 0.2;

    /// <summary>
    /// The weighted parts are raised to this before they become a percentage. Two people with quite similar taste
    /// still differ on most individual shows, so the parts run low: untouched, like-minded pairs landed around 60 and
    /// everyone else on the floor. With it, on the 48 local test members, pairs who watch the same kinds of show score
    /// 59 to 89 (median 76) and pairs who don't, 31 or under. Tuned on those members only.
    /// </summary>
    public const double Curve = 0.6;

    public const int MaxBothLove = 4;

    /// <summary>
    /// Shows pictured on the share card.
    /// </summary>
    public const int MaxCardShows = 4;
    public const int MaxBothInto = 3;
    public const int MaxWatchTogether = 6;

    /// <summary>
    /// Fewer suggestions than this aren't shown: the block is left out, not padded.
    /// </summary>
    public const int MinWatchTogether = 3;

    /// <summary>
    /// A genre is one both are into when it is at least this share of each side's love.
    /// </summary>
    private const double MinSharedGenreShare = 0.1;

    /// <summary>
    /// A genre is a difference when one side's share of it is this much more than the other's.
    /// </summary>
    private const double MinGenreGap = 0.1;

    /// <summary>
    /// Each band's lowest score, best first, with its lines. Friendly at every band: a low score is a reason to swap
    /// lists, not a put-down.
    /// </summary>
    private static readonly (int From, string[] Lines)[] Bands =
    [
        (90, ["You could share a watchlist and nobody would notice.", "One remote would do. You'd never argue over it.", "Suspiciously alike. Are you sure you're two people?"]),
        (75, ["You'd fight over the remote for about four minutes.", "Same shelf, different favourites.", "You'd finish each other's watchlists."]),
        (55, ["Plenty in common, and enough apart to surprise each other.", "A good overlap, with room to recommend.", "You agree on the big ones and argue about the rest."]),
        (35, ["Different corners of the same sofa.", "Not much overlap, which is where the good recommendations come from.", "You'd each double the other's list."]),
        (0, ["Opposites. Swap one show each and see what happens.", "Two very different watchlists: twice as much to discover.", "You'd broaden each other's lists more than anyone."])
    ];

    /// <summary>
    /// The shows of a side that are scored: its heaviest <see cref="MaxScored"/>, heaviest first.
    /// </summary>
    public static Dictionary<Guid, double> Side(IEnumerable<KeyValuePair<Guid, double>> loved) =>
        Heaviest(loved.Where(l => l.Value > 0), MaxScored).ToDictionary(l => l.Key, l => l.Value);

    /// <summary>
    /// The heaviest shows, heaviest first; shows that weigh the same go by id, so the pick is the same every time.
    /// </summary>
    private static IEnumerable<KeyValuePair<Guid, double>> Heaviest(IEnumerable<KeyValuePair<Guid, double>> shows, int count) =>
        shows.OrderByDescending(l => l.Value).ThenBy(l => l.Key).Take(count);

    /// <summary>
    /// A guest's side: each pick counts fully.
    /// </summary>
    public static Dictionary<Guid, double> Picked(IEnumerable<Guid> picks) => picks.Distinct().ToDictionary(id => id, _ => 1.0);

    /// <summary>
    /// The shows of a side that a result could name, heaviest first: what a member is shown before their side is used.
    /// </summary>
    public static List<Guid> Nameable(IReadOnlyDictionary<Guid, double> side) =>
        Heaviest(side, MaxNameable).Select(l => l.Key).ToList();

    /// <summary>
    /// The shows a result's share card pictures, and what it calls them: what the two both love when there is any, else
    /// what they could watch together. Never one side's own shows.
    /// </summary>
    public static (IReadOnlyList<MatchShowDto> Shows, string Label) CardShows(TasteMatchDto result) => result.BothLove.Count > 0
        ? (result.BothLove.Take(MaxCardShows).ToList(), "You both love")
        : (result.WatchTogether.Take(MaxCardShows).Select(p => new MatchShowDto(p.ShowId, p.Title, p.PosterUrl)).ToList(), "Watch together");

    /// <summary>
    /// Each genre's share of a side's love: a show's weight goes to each of its genres, and the shares add up to 1.
    /// </summary>
    public static Dictionary<string, double> GenreShares(IReadOnlyDictionary<Guid, double> side, Func<Guid, IReadOnlyList<string>> genresOf)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (showId, weight) in side)
        {
            foreach (var genre in genresOf(showId).Distinct())
            {
                weights[genre] = weights.GetValueOrDefault(genre) + weight;
            }
        }
        var total = weights.Values.Sum();
        return total > 0 ? weights.ToDictionary(g => g.Key, g => g.Value / total, StringComparer.Ordinal) : weights;
    }

    /// <summary>
    /// Whether they watch the same kinds of thing: the cosine of the two sides' genre shares, 0 (nothing in common) to 1.
    /// </summary>
    public static double Kinds(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        var dot = a.Sum(g => g.Value * b.GetValueOrDefault(g.Key));
        var norms = Math.Sqrt(a.Values.Sum(v => v * v)) * Math.Sqrt(b.Values.Sum(v => v * v));
        return norms > 0 ? Math.Clamp(dot / norms, 0, 1) : 0;
    }

    /// <summary>
    /// Shows both love, as a share of the smaller side: each shared show counts its smaller weight.
    /// </summary>
    public static double Shared(IReadOnlyDictionary<Guid, double> a, IReadOnlyDictionary<Guid, double> b)
    {
        var smaller = Math.Min(a.Values.Sum(), b.Values.Sum());
        return smaller > 0 ? Math.Clamp(a.Sum(l => Math.Min(l.Value, b.GetValueOrDefault(l.Key))) / smaller, 0, 1) : 0;
    }

    /// <param name="alike">How close the two sets are in meaning, 0-1, from the catalog.</param>
    public static int Score(double alike, double kinds, double shared) =>
        Math.Clamp((int)Math.Round(100 * Math.Pow(AlikeWeight * alike + KindsWeight * kinds + SharedWeight * shared, Curve)), MinScore, MaxScore);

    /// <summary>
    /// The sentence for a score: one of its band's, picked by the match's token so a pair always sees the same one.
    /// </summary>
    public static string Line(int score, string token)
    {
        var lines = Bands.First(b => score >= b.From).Lines;
        // Not string.GetHashCode: that differs between runs.
        var pick = token.Aggregate(17u, (hash, c) => unchecked(hash * 31 + c));
        return lines[(int)(pick % (uint)lines.Length)];
    }

    /// <summary>
    /// The match: the score and what may be said about it.
    /// </summary>
    /// <param name="inviter">The inviter's scored side (<see cref="Side"/>).</param>
    /// <param name="friend">The friend's.</param>
    /// <param name="unnamed">Shows either asked not to have named; scored all the same.</param>
    /// <param name="inviterSeeds">The inviter's shows the catalog seeded the suggestions with: what a suggestion can be said
    /// to take from them.</param>
    /// <param name="suggested">The catalog's suggestions, best first, none known to either.</param>
    /// <param name="shows">Title, poster and genres of every show involved; one that's missing is not named.</param>
    public static TasteMatchDto Of(string token, IReadOnlyDictionary<Guid, double> inviter, IReadOnlyDictionary<Guid, double> friend,
        IReadOnlySet<Guid> unnamed, double alike, IReadOnlyCollection<Guid> inviterSeeds, IReadOnlyCollection<Guid> friendSeeds,
        IReadOnlyList<Guid> suggested, bool loose, IReadOnlyDictionary<Guid, MatchShow> shows)
    {
        IReadOnlyList<string> GenresOf(Guid id) => shows.TryGetValue(id, out var show) ? show.Genres : [];
        var (inviterGenres, friendGenres) = (GenreShares(inviter, GenresOf), GenreShares(friend, GenresOf));
        var score = Score(alike, Kinds(inviterGenres, friendGenres), Shared(inviter, friend));

        var bothLove = inviter.Keys.Where(friend.ContainsKey)
            .Where(id => !unnamed.Contains(id) && shows.ContainsKey(id))
            .OrderByDescending(id => Math.Min(inviter[id], friend[id])).ThenByDescending(id => inviter[id] + friend[id]).ThenBy(id => shows[id].Title, StringComparer.Ordinal)
            .ThenBy(id => id)
            .Take(MaxBothLove)
            .Select(id => new MatchShowDto(id, shows[id].Title, shows[id].PosterUrl))
            .ToList();

        var bothInto = inviterGenres.Keys.Where(friendGenres.ContainsKey)
            .Where(g => Math.Min(inviterGenres[g], friendGenres[g]) >= MinSharedGenreShare)
            .OrderByDescending(g => Math.Min(inviterGenres[g], friendGenres[g])).ThenBy(g => g, StringComparer.Ordinal)
            .Take(MaxBothInto)
            .ToList();

        // The seeds' genres say what a suggestion takes from each side.
        var inviterSeedGenres = inviterSeeds.SelectMany(GenresOf).ToHashSet(StringComparer.Ordinal);
        var friendSeedGenres = friendSeeds.SelectMany(GenresOf).ToHashSet(StringComparer.Ordinal);
        var together = suggested
            .Where(id => shows.ContainsKey(id) && !inviter.ContainsKey(id) && !friend.ContainsKey(id))
            .Take(MaxWatchTogether)
            .Select(id =>
            {
                var genres = shows[id].Genres;
                return new MatchPickDto(id, shows[id].Title, shows[id].PosterUrl,
                    genres.Where(g => inviterSeedGenres.Contains(g) && !friendSeedGenres.Contains(g)).ToList(),
                    genres.Where(g => friendSeedGenres.Contains(g) && !inviterSeedGenres.Contains(g)).ToList(),
                    genres.Where(g => inviterSeedGenres.Contains(g) && friendSeedGenres.Contains(g)).ToList());
            })
            .ToList();

        return new TasteMatchDto(score, Line(score, token), bothLove, bothInto, Differ(inviterGenres, friendGenres),
            together.Count >= MinWatchTogether ? together : [], loose);
    }

    /// <summary>
    /// The genre each side watches most beyond the other; null unless both have one at least <see cref="MinGenreGap"/> ahead.
    /// </summary>
    internal static MatchDifferenceDto? Differ(IReadOnlyDictionary<string, double> inviter, IReadOnlyDictionary<string, double> friend)
    {
        static string? Ahead(IReadOnlyDictionary<string, double> mine, IReadOnlyDictionary<string, double> theirs) => mine
            .Select(g => (Genre: g.Key, Gap: g.Value - theirs.GetValueOrDefault(g.Key)))
            .Where(g => g.Gap >= MinGenreGap)
            .OrderByDescending(g => g.Gap).ThenBy(g => g.Genre, StringComparer.Ordinal)
            .Select(g => g.Genre)
            .FirstOrDefault();

        return Ahead(inviter, friend) is { } a && Ahead(friend, inviter) is { } b ? new MatchDifferenceDto(a, b) : null;
    }
}

/// <summary>
/// What a match needs to know of a show to name it.
/// </summary>
public sealed record MatchShow(string Title, string? PosterUrl, IReadOnlyList<string> Genres);

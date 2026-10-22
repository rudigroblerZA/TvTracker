using System.Text.RegularExpressions;

namespace Catalog.SyncWorker.Nanogenres;

/// <param name="Embedding">The show's text embedding (unit length) from the current model; null when not embedded yet.</param>
/// <param name="Keywords">The catalog's keywords: the provider's show type ("scripted", "animation", "reality", …) and language.</param>
public record NanogenreShow(Guid ShowId, IReadOnlyList<string> Genres, IReadOnlyList<string> Keywords, float[]? Embedding);

/// <param name="Source">"cluster" or "genres".</param>
/// <param name="ShowIds">Members, in input order.</param>
public record DiscoveredNanogenre(string Slug, string Name, string Source, IReadOnlyList<Guid> ShowIds);

/// <summary>
/// Finds nanogenres in the catalog itself, with no list of them written down:
/// <list type="number">
/// <item><b>Clusters:</b> shows are grouped by average-linkage clustering on a mix of their text embeddings (what
/// they're about) and their genres and type (what kind of show), weighted <see cref="GenreWeight"/> to genres, so a
/// sports comedy groups with comedies about sport, not with sports documentaries. Groups larger than
/// <see cref="MaxSize"/> are split again with a tighter cut; groups under <see cref="MinSize"/> are dropped.</item>
/// <item><b>More than one each:</b> a show also joins any group it's as close to, on average, as the group's typical
/// member is. Wednesday is both a supernatural and a mystery comedy.</item>
/// <item><b>Names:</b> from the provider's specific genres (Legal, Medical, Sports, Western, …) shared by most of a
/// group, on the group's base (comedy, drama, thriller, horror, reality, documentary, anime): "Medical drama",
/// "Animated family comedy". A group with no specific genre in common has nothing honest to be called, so it's dropped.
/// Words from the shows' summaries were tried as themes, but were too noisy ("greatest", "these") to show.</item>
/// <item><b>Small genre pairings:</b> an uncommon pairing shared by <see cref="MinPairSize"/> to <see cref="MaxPairSize"/>
/// shows, where no cluster already covers them, is a nanogenre too ("Sports comedy": Ted Lasso and The League). Larger
/// pairings ("Crime drama", 113 shows) aren't nano.</item>
/// </list>
/// The parameters were tuned on the 625-show development catalog with dmr/nomic-embed-text-v1.5; other models' embedding
/// distances are scaled by their <c>MaxDistance</c> to match (see <see cref="Discover"/>).
/// </summary>
public static partial class NanogenreDiscovery
{
    public const double GenreWeight = 0.6;
    public const double Cut = 0.25;
    private const double CutStep = 0.03;
    public const int MinSize = 3;
    public const int MaxSize = 30;
    public const int MinPairSize = 2;
    public const int MaxPairSize = 15;

    /// <summary>A name's genre pairing must cover at least this share of a group.</summary>
    private const double NameCover = 0.6;

    /// <summary>The model the cut was tuned with has MaxDistance 0.38; distances from other models are scaled to it.</summary>
    private const double ReferenceMaxDistance = 0.38;

    private static readonly string[] Kinds = ["scripted", "animation", "reality", "documentary", "game show", "talk show", "variety"];

    /// <summary>The provider's specific genres, as they read before a base: "sports comedy", "spy thriller".</summary>
    private static readonly Dictionary<string, string> Modifiers = new()
    {
        ["Legal"] = "legal", ["Medical"] = "medical", ["Crime"] = "crime", ["Science-Fiction"] = "sci-fi", ["Fantasy"] = "fantasy",
        ["Supernatural"] = "supernatural", ["Family"] = "family", ["Romance"] = "romantic", ["Sports"] = "sports",
        ["Espionage"] = "spy", ["War"] = "war", ["Western"] = "western", ["History"] = "historical", ["Music"] = "music",
        ["Mystery"] = "mystery", ["Action"] = "action", ["Adventure"] = "adventure", ["Food"] = "cooking",
        ["DIY"] = "home-renovation", ["Travel"] = "travel", ["Nature"] = "nature", ["Children"] = "kids'"
    };

    /// <param name="modelMaxDistance">The embedding model's semantic-search cutoff, a measure of how it spreads distances.</param>
    public static List<DiscoveredNanogenre> Discover(IReadOnlyList<NanogenreShow> shows, double modelMaxDistance)
    {
        var kinds = shows.Select(s => s.Keywords.FirstOrDefault(k => Kinds.Contains(k))).ToArray();
        var features = GenreFeatures(shows, kinds);
        var scale = ReferenceMaxDistance / modelMaxDistance;
        var embedded = Enumerable.Range(0, shows.Count).Where(i => shows[i].Embedding is not null).ToList();

        double Distance(int a, int b)
        {
            var text = 1 - Dot(shows[a].Embedding!, shows[b].Embedding!);
            var kind = 1 - Dot(features[a], features[b]);
            return (1 - GenreWeight) * text * scale + GenreWeight * kind;
        }

        var genreFrequency = shows.SelectMany(s => s.Genres).GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count());
        var pairings = Pairings(shows, kinds);

        // Clusters, with members who join for being as close as the typical member.
        var named = new Dictionary<string, (string Source, HashSet<int> Members)>();
        foreach (var core in Split(embedded, Distance, Cut))
        {
            var members = core.ToHashSet();
            var typical = Median(core.Select(i => core.Where(j => j != i).Average(j => Distance(i, j))).ToList());
            foreach (var other in embedded.Where(o => !members.Contains(o)))
            {
                if (core.Average(j => Distance(other, j)) <= typical)
                {
                    members.Add(other);
                }
            }

            var names = NamesFor(core, shows, kinds, pairings, genreFrequency);
            if (names.FirstOrDefault(n => !named.ContainsKey(n)) is { } name)
            {
                named[name] = ("cluster", members);
            }
            else if (names.Count > 0)
            {
                // Every name it could have is taken by a group like it: they're one nanogenre.
                named[names[0]].Members.UnionWith(members);
            }
        }

        foreach (var ((modifier, @base), members) in pairings)
        {
            var name = Capitalize($"{modifier} {@base}");
            if (members.Count is >= MinPairSize and <= MaxPairSize && !named.ContainsKey(name)
                && !named.Values.Any(n => (double)n.Members.Intersect(members).Count() / members.Count >= 0.5))
            {
                named[name] = ("genres", members);
            }
        }

        return named
            .Select(n => new DiscoveredNanogenre(Slug(n.Key), n.Key, n.Value.Source, n.Value.Members.Order().Select(i => shows[i].ShowId).ToList()))
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The names a group could take, best first: its most widely shared specific genre on its base (rarer genres win
    /// ties, being more telling), then two-genre names for when that's taken. Empty when nothing specific is shared.
    /// </summary>
    private static List<string> NamesFor(
        List<int> core, IReadOnlyList<NanogenreShow> shows, string?[] kinds,
        Dictionary<(string Modifier, string Base), HashSet<int>> pairings, Dictionary<string, int> genreFrequency)
    {
        var animated = core.Count(i => kinds[i] == "animation" && !shows[i].Genres.Contains("Anime")) >= NameCover * core.Count;
        var prefix = animated ? "animated " : "";
        var covering = pairings
            .Select(p => (p.Key, Cover: (double)core.Count(p.Value.Contains) / core.Count))
            .Where(p => p.Cover >= NameCover)
            .OrderByDescending(p => p.Cover)
            .ThenBy(p => genreFrequency.GetValueOrDefault(Modifiers.First(m => m.Value == p.Key.Modifier).Key))
            .Select(p => p.Key)
            .ToList();

        if (covering.Count == 0)
        {
            return animated ? [Capitalize($"{prefix}comedy")] : [];
        }

        var first = covering[0];
        var names = new List<string> { Capitalize($"{prefix}{first.Modifier} {first.Base}") };
        names.AddRange(covering.Skip(1)
            .Where(p => p.Base == first.Base && p.Modifier != first.Modifier)
            .Select(p => Capitalize($"{prefix}{p.Modifier} {first.Modifier} {first.Base}")));
        return names;
    }

    /// <summary>Every (specific genre, base) pairing in the catalog, with the shows that have it.</summary>
    private static Dictionary<(string Modifier, string Base), HashSet<int>> Pairings(IReadOnlyList<NanogenreShow> shows, string?[] kinds)
    {
        var pairings = new Dictionary<(string, string), HashSet<int>>();
        for (var i = 0; i < shows.Count; i++)
        {
            if (BaseOf(shows[i].Genres, kinds[i]) is not { } @base)
            {
                continue;
            }

            foreach (var genre in shows[i].Genres)
            {
                // "Mystery thriller" says nothing a thriller doesn't.
                if (Modifiers.TryGetValue(genre, out var modifier) && !(genre == "Mystery" && @base == "thriller"))
                {
                    if (!pairings.TryGetValue((modifier, @base), out var members))
                    {
                        pairings[(modifier, @base)] = members = [];
                    }
                    members.Add(i);
                }
            }
        }
        return pairings;
    }

    /// <summary>What kind of show it is at heart: the noun a nanogenre's name ends with.</summary>
    private static string? BaseOf(IReadOnlyList<string> genres, string? kind) =>
        genres.Contains("Anime") ? "anime"
        : kind switch
        {
            "reality" => "reality",
            "documentary" => "documentary",
            "game show" => "game show",
            _ => genres.Contains("Comedy") ? "comedy"
                : genres.Contains("Thriller") ? "thriller"
                : genres.Contains("Horror") ? "horror"
                : genres.Contains("Drama") ? "drama"
                : null
        };

    /// <summary>
    /// Flat clusters at <paramref name="cut"/>, with any larger than <see cref="MaxSize"/> split again more tightly.
    /// Groups under <see cref="MinSize"/> are left out.
    /// </summary>
    private static List<List<int>> Split(List<int> members, Func<int, int, double> distance, double cut)
    {
        var result = new List<List<int>>();
        foreach (var cluster in AverageLinkage(members, distance, cut))
        {
            if (cluster.Count > MaxSize && cut - CutStep > 0)
            {
                result.AddRange(Split(cluster, distance, cut - CutStep));
            }
            else if (cluster.Count >= MinSize)
            {
                result.Add(cluster);
            }
        }
        return result;
    }

    /// <summary>
    /// Average-linkage clustering by the nearest-neighbour chain, in O(n²) time and memory, cut at <paramref name="cut"/>.
    /// Average linkage never merges below an earlier merge, so every merge at or under the cut belongs to the flat
    /// clustering, whatever order the chain found them in.
    /// </summary>
    private static List<List<int>> AverageLinkage(List<int> members, Func<int, int, double> distance, double cut)
    {
        var n = members.Count;
        var d = new double[n, n];
        for (var a = 0; a < n; a++)
        {
            for (var b = a + 1; b < n; b++)
            {
                d[a, b] = d[b, a] = distance(members[a], members[b]);
            }
        }

        var size = Enumerable.Repeat(1, n).ToArray();
        var active = Enumerable.Repeat(true, n).ToArray();
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);

        var chain = new List<int>();
        for (var remaining = n; remaining > 1;)
        {
            if (chain.Count == 0)
            {
                chain.Add(Array.IndexOf(active, true));
            }

            var a = chain[^1];
            var previous = chain.Count > 1 ? chain[^2] : -1;
            var nearest = previous;
            var best = previous >= 0 ? d[a, previous] : double.MaxValue;
            for (var c = 0; c < n; c++)
            {
                if (active[c] && c != a && d[a, c] < best)
                {
                    (nearest, best) = (c, d[a, c]);
                }
            }

            if (nearest != previous)
            {
                chain.Add(nearest);
                continue;
            }

            // a and its predecessor are each other's nearest: merge them, keeping the new cluster at a.
            chain.RemoveRange(chain.Count - 2, 2);
            var b = previous;
            for (var c = 0; c < n; c++)
            {
                if (active[c] && c != a && c != b)
                {
                    d[a, c] = d[c, a] = (size[a] * d[a, c] + size[b] * d[b, c]) / (size[a] + size[b]);
                }
            }
            size[a] += size[b];
            active[b] = false;
            remaining--;
            if (best <= cut)
            {
                parent[Find(b)] = Find(a);
            }
        }

        return Enumerable.Range(0, n).GroupBy(Find).Select(g => g.Select(i => members[i]).ToList()).ToList();
    }

    /// <summary>Each show's genres and type as a unit vector, so two shows of the same kind score 1.</summary>
    private static float[][] GenreFeatures(IReadOnlyList<NanogenreShow> shows, string?[] kinds)
    {
        var axes = shows.SelectMany(s => s.Genres).Concat(kinds.OfType<string>().Select(k => $"kind:{k}")).Distinct().ToList();
        var index = axes.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i);
        return shows.Select((s, i) =>
        {
            var v = new float[axes.Count];
            foreach (var g in s.Genres)
            {
                v[index[g]] = 1;
            }
            if (kinds[i] is { } kind)
            {
                v[index[$"kind:{kind}"]] = 1;
            }
            var length = MathF.Sqrt(v.Sum(x => x * x));
            return length == 0 ? v : v.Select(x => x / length).ToArray();
        }).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    private static string Capitalize(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    public static string Slug(string name) => NonWord().Replace(name.ToLowerInvariant().Replace("'", ""), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}

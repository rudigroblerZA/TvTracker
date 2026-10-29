using System.Text.Json;
using Analytics.Data;
using TvTracker.IntegrationEvents;

namespace Analytics.Worker.Insights;

/// <summary>
/// Creator fingerprints (<see cref="CreatorFingerprint"/>): for everyone credited as creator or developer of at least
/// <see cref="MinShows"/> shows with DNA, their shows' DNA averaged trait by trait. Executive producers are left out:
/// a producer's credits follow a production company, not a style.
/// </summary>
public sealed class CreatorFingerprintList : IListInsight
{
    private const int MinShows = 2;

    /// <summary>A signature trait is distinctive on average, and at least this share of their shows lean that way.</summary>
    private const double SignatureAgreement = 2.0 / 3;

    /// <summary>Shows and creators match the fingerprint from this DNA similarity, the same bar as DNA twins.</summary>
    private const double MinSimilarity = 0.85;

    private const int MaxMatches = 8;
    private const int MaxKindred = 5;

    private static readonly string[] MakerRoles = ["Creator", "Developer"];

    public ListBuilder Begin(InsightSources sources) => new Builder(sources);

    private sealed record Fingerprint(Guid PersonId, string Name, List<Guid> ShowIds, Dictionary<string, double> Traits, List<SignatureTrait> Signature);

    private sealed class Builder(InsightSources sources) : ListBuilder<CreatorFingerprintRow>(db => db.CreatorFingerprints)
    {
        private readonly Dictionary<Guid, Dictionary<string, double>> dna = [];

        public override string Name => "creators";

        public override void Add(ShowInsightContext show, ShowInsights insights)
        {
            if (insights.Dna is { Traits.Count: > 0 } showDna)
            {
                dna[show.ShowId] = showDna.Traits.ToDictionary(t => t.Key, t => t.Value);
            }
        }

        protected override IReadOnlyCollection<CreatorFingerprintRow> Rows()
        {
            var makers = sources.Creatives
                .Where(c => c.Roles.Any(MakerRoles.Contains) && sources.Shows.ContainsKey(c.ShowId))
                .ToList();
            var makersByShow = makers.ToLookup(c => c.ShowId, c => c.PersonId);

            var fingerprints = makers
                .Where(c => dna.ContainsKey(c.ShowId))
                .GroupBy(c => c.PersonId)
                .Select(g => FingerprintOf(g.Key, g.First().Name, g.Select(c => c.ShowId).Distinct().ToList()))
                .Where(f => f.ShowIds.Count >= MinShows)
                .ToList();

            return fingerprints.Select(f =>
                {
                    var document = new CreatorFingerprint(
                        f.PersonId,
                        f.Name,
                        sources.ComputedAtUtc,
                        f.ShowIds.Select(id => sources.Shows[id]).Select(s => new FingerprintShow(s.ShowId, s.Title, s.PosterUrl)).ToList(),
                        f.Traits.ToDictionary(t => t.Key, t => InsightMath.Round(t.Value)),
                        f.Signature,
                        RangeOf(f),
                        MatchesFor(f, makersByShow),
                        KindredOf(f, fingerprints));
                    return new CreatorFingerprintRow
                    {
                        PersonId = f.PersonId,
                        Document = InsightDocuments.Serialize(document),
                        ComputedAtUtc = sources.ComputedAtUtc
                    };
                })
                .ToList();
        }

        private Fingerprint FingerprintOf(Guid personId, string name, List<Guid> showIds)
        {
            var traits = new Dictionary<string, double>();
            var signature = new List<(double Strength, SignatureTrait Trait)>();
            foreach (var trait in DnaTraits.All)
            {
                var values = showIds.Where(id => dna[id].ContainsKey(trait.Key)).Select(id => dna[id][trait.Key]).ToList();
                if (values.Count < MinShows)
                {
                    continue;
                }

                var average = values.Average();
                traits[trait.Key] = average;
                if (DnaTraits.LabelFor(trait.Key, average) is { } label && DnaTraits.Distinctiveness(trait.Key, average) > 0)
                {
                    var agreeing = values.Count(v => DnaTraits.LabelFor(trait.Key, v) == label);
                    if (agreeing >= Math.Max(MinShows, Math.Ceiling(values.Count * SignatureAgreement)))
                    {
                        signature.Add((trait.Weight * DnaTraits.Distinctiveness(trait.Key, average), new SignatureTrait(trait.Key, label, agreeing, values.Count)));
                    }
                }
            }

            return new Fingerprint(personId, name, showIds.OrderByDescending(id => sources.Shows[id].Popularity).ToList(), traits,
                signature.OrderByDescending(s => s.Strength).Select(s => s.Trait).ToList());
        }

        /// <summary>Traits where at least one of their shows is at each end: "Short episodes to hour-long episodes".</summary>
        private List<string> RangeOf(Fingerprint f) => DnaTraits.All
            .Where(t => f.Signature.All(s => s.Key != t.Key))
            .Select(t => (Trait: t, Labels: f.ShowIds.Where(id => dna[id].ContainsKey(t.Key)).Select(id => DnaTraits.LabelFor(t.Key, dna[id][t.Key])).ToHashSet()))
            .Where(x => x.Labels.Contains(x.Trait.Low) && x.Labels.Contains(x.Trait.High))
            .Select(x => $"{x.Trait.Low} to {x.Trait.High.ToLowerInvariant()}")
            .ToList();

        /// <summary>
        /// Other people's shows close to the fingerprint that share its signature (two traits, or its only one). Only
        /// shows with a creator or developer credit count: TVmaze rarely credits one on reality, talk and news shows,
        /// and Analytics has no kind of show to tell them apart otherwise (a drama's style matched The Kardashians).
        /// </summary>
        private List<FingerprintMatch> MatchesFor(Fingerprint f, ILookup<Guid, Guid> makersByShow)
        {
            if (f.Signature.Count == 0)
            {
                return [];
            }

            var needed = Math.Min(2, f.Signature.Count);
            return dna
                .Where(d => !f.ShowIds.Contains(d.Key) && makersByShow[d.Key].Any() && !makersByShow[d.Key].Contains(f.PersonId))
                .Select(d => (ShowId: d.Key, Similarity: DnaTraits.Similarity(f.Traits, d.Value),
                    Shared: f.Signature.Where(s => d.Value.TryGetValue(s.Key, out var v) && DnaTraits.LabelFor(s.Key, v) == s.Label && DnaTraits.Distinctiveness(s.Key, v) > 0).Select(s => s.Label).ToList()))
                .Where(m => m.Similarity >= MinSimilarity && m.Shared.Count >= needed)
                .OrderByDescending(m => m.Similarity)
                .ThenByDescending(m => sources.Shows[m.ShowId].Popularity)
                .Take(MaxMatches)
                .Select(m => new FingerprintMatch(m.ShowId, sources.Shows[m.ShowId].Title, sources.Shows[m.ShowId].PosterUrl, InsightMath.Round(m.Similarity!.Value), m.Shared))
                .ToList();
        }

        /// <summary>Creators with a close fingerprint and a signature trait in common, who never made a show together.</summary>
        private List<KindredCreator> KindredOf(Fingerprint f, List<Fingerprint> all) => all
            .Where(o => o.PersonId != f.PersonId && !o.ShowIds.Intersect(f.ShowIds).Any())
            .Select(o => (Other: o, Similarity: DnaTraits.Similarity(f.Traits, o.Traits),
                Shared: f.Signature.Select(s => s.Label).Intersect(o.Signature.Select(s => s.Label)).ToList()))
            .Where(k => k.Similarity >= MinSimilarity && k.Shared.Count > 0)
            .OrderByDescending(k => k.Similarity)
            .Take(MaxKindred)
            .Select(k => new KindredCreator(k.Other.PersonId, k.Other.Name, InsightMath.Round(k.Similarity!.Value), k.Shared,
                k.Other.ShowIds.Select(id => sources.Shows[id].Title).ToList()))
            .ToList();
    }
}

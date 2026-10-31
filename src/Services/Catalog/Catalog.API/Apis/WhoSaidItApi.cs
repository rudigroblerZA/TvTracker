using System.Text.RegularExpressions;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="EpisodeIds">Episodes the player has watched; lines come only from these. Empty: popular shows' first episodes.</param>
public record WhoSaidItRequest(IReadOnlyList<Guid>? EpisodeIds, int Count = WhoSaidItApi.DefaultCount);

/// <param name="FromWatched">The questions come from the player's watched episodes, not the popular fallback.</param>
public record WhoSaidItRoundDto(bool FromWatched, IReadOnlyList<WhoSaidItQuestionDto> Questions);

/// <param name="Line">One line, stage directions taken out.</param>
/// <param name="Options">Three or four characters of the show, in random order; one is <paramref name="Answer"/>.</param>
/// <param name="PageUrl">The show's Wikiquote page, credited with the line (CC BY-SA).</param>
public record WhoSaidItQuestionDto(Guid ShowId, string ShowTitle, string? PosterUrl, Guid EpisodeId, int SeasonNumber, int EpisodeNumber,
    string Line, IReadOnlyList<string> Options, string Answer, string PageUrl);

/// <summary>
/// Who said it?: a line from Memorable lines (<see cref="QuotesApi"/>), and which character said it. Lines come only from
/// episodes the player says they've watched, so a question can't spoil anything, and the options are other characters
/// with lines in those same episodes, so it can't introduce a character the player hasn't met. With no watched lines (or
/// signed out), it takes popular shows' first episodes with lines, which give the least away.
/// <list type="bullet">
/// <item><b>Names:</b> Wikiquote writes "Sherlock" in one episode and "Sherlock Holmes" in the next, so names whose words
/// sit inside another's are one character, shown by the fuller name.</item>
/// <item><b>Fair lines:</b> 20-200 characters once stage directions are out, and not naming any of the options (a line
/// that says "Walter" isn't Walter guessing). Options and answers are recurring characters, with
/// <see cref="MinLines"/> or more lines in the allowed episodes; a one-off "Uniform" can't be guessed.</item>
/// <item><b>Answers in the response:</b> stateless, on trust, like Guess the show; it's a game.</item>
/// </list>
/// </summary>
public static partial class WhoSaidItApi
{
    public const int DefaultCount = 5;
    private const int MaxCount = 10;
    private const int MaxEpisodeIds = 5000;
    private const int FallbackShows = 30;
    private const int MinLine = 20;
    private const int MaxLine = 200;

    /// <summary>Lines a character needs in the allowed episodes to be an option or an answer.</summary>
    private const int MinLines = 2;

    private sealed record Candidate(Guid ShowId, int Season, int Episode, string Speaker, string Line);

    public static void MapWhoSaidItApi(this RouteGroupBuilder api) =>
        api.MapPost("/quiz/who-said-it", PlayAsync).RequireFeature(FeatureFlags.WhoSaidIt);

    private static async Task<Results<Ok<WhoSaidItRoundDto>, BadRequest<string>>> PlayAsync(WhoSaidItRequest request, CatalogContext db,
        CancellationToken ct)
    {
        if (request.EpisodeIds is { Count: > MaxEpisodeIds })
        {
            return TypedResults.BadRequest($"At most {MaxEpisodeIds} episodes.");
        }
        var count = Math.Clamp(request.Count, 1, MaxCount);

        if (request.EpisodeIds is { Count: > 0 } ids)
        {
            var watched = await db.Episodes.AsNoTracking().Where(e => ids.Contains(e.Id))
                .Select(e => new { e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber })
                .ToListAsync(ct);
            var episodes = watched.DistinctBy(e => (e.ShowId, e.SeasonNumber, e.EpisodeNumber))
                .ToDictionary(e => (e.ShowId, e.SeasonNumber, e.EpisodeNumber), e => e.Id);
            var showIds = episodes.Keys.Select(k => k.ShowId).Distinct().ToList();
            var questions = await BuildAsync(db, showIds, (show, season, episode) => episodes.GetValueOrDefault((show, season, episode)), count, ct);
            if (questions.Count > 0)
            {
                return TypedResults.Ok(new WhoSaidItRoundDto(true, questions));
            }
        }

        // Popular shows' first episodes with lines.
        var popular = await db.QuoteSources.AsNoTracking()
            .Where(q => q.FetchedAtUtc != null && db.Quotes.Any(x => x.ShowId == q.ShowId))
            .OrderByDescending(q => db.Shows.Where(s => s.Id == q.ShowId).Select(s => s.Popularity).FirstOrDefault())
            .Select(q => q.ShowId)
            .Take(FallbackShows)
            .ToListAsync(ct);
        var firstEpisodes = (await db.Quotes.AsNoTracking()
                .Where(q => popular.Contains(q.ShowId) && q.SeasonNumber > 0 && q.EpisodeNumber != null)
                .Select(q => new { q.ShowId, Season = q.SeasonNumber!.Value, Episode = q.EpisodeNumber!.Value })
                .Distinct()
                .ToListAsync(ct))
            .GroupBy(q => q.ShowId)
            .ToDictionary(g => g.Key, g => g.OrderBy(q => q.Season).ThenBy(q => q.Episode).Select(q => (q.Season, q.Episode)).First());
        var catalog = (await db.Episodes.AsNoTracking().Where(e => popular.Contains(e.ShowId) && e.SeasonNumber > 0)
                .Select(e => new { e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber })
                .ToListAsync(ct))
            .DistinctBy(e => (e.ShowId, e.SeasonNumber, e.EpisodeNumber))
            .ToDictionary(e => (e.ShowId, e.SeasonNumber, e.EpisodeNumber), e => e.Id);
        var fallback = await BuildAsync(db, popular,
            (show, season, episode) => firstEpisodes.TryGetValue(show, out var first) && first == (season, episode)
                ? catalog.GetValueOrDefault((show, season, episode)) : Guid.Empty,
            count, ct);
        return TypedResults.Ok(new WhoSaidItRoundDto(false, fallback));
    }

    /// <summary>
    /// Questions from the shows' lines in the episodes <paramref name="episodeOf"/> allows (it gives the catalog's
    /// episode id, or empty to leave the episode out), taking shows in turn so a round isn't all one show.
    /// </summary>
    private static async Task<List<WhoSaidItQuestionDto>> BuildAsync(CatalogContext db, List<Guid> showIds,
        Func<Guid, int, int, Guid> episodeOf, int count, CancellationToken ct)
    {
        if (showIds.Count == 0)
        {
            return [];
        }
        var quotes = await db.Quotes.AsNoTracking()
            .Where(q => showIds.Contains(q.ShowId) && q.SeasonNumber != null && q.EpisodeNumber != null)
            .Select(q => new { q.ShowId, Season = q.SeasonNumber!.Value, Episode = q.EpisodeNumber!.Value, q.Speakers, q.Lines })
            .ToListAsync(ct);
        var allowed = quotes.Where(q => episodeOf(q.ShowId, q.Season, q.Episode) != Guid.Empty).ToList();
        if (allowed.Count == 0)
        {
            return [];
        }
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.PosterUrl })
            .ToDictionaryAsync(s => s.Id, ct);
        var pages = await db.QuoteSources.AsNoTracking().Where(q => showIds.Contains(q.ShowId) && q.Page != null)
            .ToDictionaryAsync(q => q.ShowId, q => ShowQuoteSource.PageUrl(q.Page!), ct);

        // Per show: its characters (names merged), and the fair lines each said.
        var pools = new List<(Guid ShowId, List<string> Characters, List<Candidate> Lines)>();
        foreach (var show in allowed.GroupBy(q => q.ShowId))
        {
            var spoken = show.SelectMany(q => q.Speakers.Zip(q.Lines, (speaker, line) => (Quote: q, Speaker: speaker, Line: line)))
                .Where(x => x.Speaker.Length > 0)
                .ToList();
            var names = Canonical(spoken.Select(x => x.Speaker));
            // Recurring characters only: a one-off "Uniform" or "Waiter" can't be guessed.
            var characters = spoken.GroupBy(x => names[x.Speaker]).Where(g => g.Count() >= MinLines).Select(g => g.Key).ToList();
            if (characters.Count < 3)
            {
                continue;
            }
            var lines = spoken
                .Select(x => new Candidate(show.Key, x.Quote.Season, x.Quote.Episode, names[x.Speaker], Direction().Replace(x.Line, " ").Trim()))
                .Select(c => c with { Line = Spaces().Replace(c.Line, " ") })
                .Where(c => characters.Contains(c.Speaker) && c.Line.Length is >= MinLine and <= MaxLine
                    && !characters.Any(name => Names(c.Line, name)))
                .ToList();
            if (lines.Count > 0)
            {
                pools.Add((show.Key, characters, lines));
            }
        }

        var questions = new List<WhoSaidItQuestionDto>();
        var used = new HashSet<string>();
        var order = pools.OrderBy(_ => Random.Shared.Next()).ToList();
        for (var round = 0; questions.Count < count && round < count; round++)
        {
            foreach (var (showId, characters, lines) in order)
            {
                if (questions.Count >= count)
                {
                    break;
                }
                var pick = lines.Where(l => !used.Contains(l.Line)).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
                if (pick is null)
                {
                    continue;
                }
                used.Add(pick.Line);
                var options = characters.Where(c => c != pick.Speaker).OrderBy(_ => Random.Shared.Next()).Take(3)
                    .Append(pick.Speaker).OrderBy(_ => Random.Shared.Next()).ToList();
                var show = shows[showId];
                questions.Add(new WhoSaidItQuestionDto(showId, show.Title, show.PosterUrl, episodeOf(showId, pick.Season, pick.Episode),
                    pick.Season, pick.Episode, pick.Line, options, pick.Speaker, pages.GetValueOrDefault(showId) ?? ""));
            }
        }
        return questions;
    }

    /// <summary>Each speaker as written, to the character it is: names whose words sit inside a fuller one's join it.</summary>
    private static Dictionary<string, string> Canonical(IEnumerable<string> speakers)
    {
        var counts = speakers.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var fullest = counts.Keys.OrderByDescending(n => Words(n).Count).ThenByDescending(n => counts[n]).ToList();
        var map = new Dictionary<string, string>();
        foreach (var name in counts.Keys)
        {
            var words = Words(name);
            // The fullest name containing all of this one's words; a name inside two different fuller ones stays itself.
            var fuller = fullest.Where(other => other != name && words.IsSubsetOf(Words(other))).ToList();
            map[name] = fuller.Count == 1 || fuller.Select(Words).Distinct(HashSet<string>.CreateSetComparer()).Count() == 1
                ? fuller.FirstOrDefault() ?? name
                : name;
        }
        return map;
    }

    private static HashSet<string> Words(string name) =>
        Word().Matches(name).Select(m => m.Value.ToLowerInvariant()).ToHashSet();

    /// <summary>The line says one of the character's name words (three letters or more).</summary>
    private static bool Names(string line, string character)
    {
        var said = Words(line);
        return Words(character).Any(w => w.Length >= 3 && said.Contains(w));
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Direction();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Word();
}

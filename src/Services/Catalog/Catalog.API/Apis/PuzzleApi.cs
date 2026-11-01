using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Catalog.API.Apis;

/// <param name="Number">The puzzle's number: 1 on <see cref="PuzzleApi.FirstDay"/>, one more each day.</param>
/// <param name="Clues">The clues given before any guess.</param>
public record PuzzleDto(int Number, DateOnly Date, int MaxGuesses, IReadOnlyList<PuzzleClueDto> Clues);

/// <param name="AfterWrong">How many wrong guesses unlock it (0: from the start).</param>
public record PuzzleClueDto(string Key, int AfterWrong, string Text);

/// <param name="Attempt">Which guess this is, 1 to <see cref="PuzzleApi.MaxGuesses"/>; the last one reveals the answer.</param>
public record PuzzleGuessRequest(Guid ShowId, int Attempt);

/// <param name="Clues">Every clue unlocked after this guess.</param>
/// <param name="Answer">Once the puzzle is over: solved, or out of guesses.</param>
public record PuzzleGuessDto(bool Correct, PuzzleFeedbackDto Feedback, IReadOnlyList<PuzzleClueDto> Clues, PuzzleShowDto? Answer);

/// <summary>How the guessed show compares with the answer, field by field.</summary>
/// <param name="Year">"same", or "later" / "earlier" when the answer premiered after / before the guess.</param>
/// <param name="YearClose">Within <see cref="PuzzleApi.CloseYears"/> years.</param>
/// <param name="Network">"same", "service" (a different network on the same streaming service) or "different".</param>
/// <param name="Seasons">"same", or "more" / "fewer" when the answer has more / fewer seasons than the guess.</param>
/// <param name="SharedCast">Actors in both shows' main casts.</param>
public record PuzzleFeedbackDto(PuzzleShowDto Guess, int? GuessYear, string Year, bool YearClose, string? GuessNetwork, string Network,
    IReadOnlyList<PuzzleGenreDto> Genres, string? GuessKind, bool SameKind, int GuessSeasons, string Seasons, bool SeasonsClose,
    string GuessStatus, bool SameStatus, IReadOnlyList<string> SharedCast);

public record PuzzleGenreDto(string Genre, bool Shared);

public record PuzzleShowDto(Guid Id, string Title, string? PosterUrl);

/// <summary>
/// Guess the show: one popular show a day, the same for everyone, found in six guesses. Each guess is compared with the
/// answer (premiere year, network, genres, kind, seasons, status, shared actors), and each wrong one unlocks a clue
/// (decade, network, a character, the lead).
/// <list type="bullet">
/// <item><b>The day's show</b> is the pool's lowest hash of date and show id, skipping the last 30 days' shows, so it
/// needs no table and every process agrees. The pool is ~230 popular English-language scripted and animated shows.
/// Each process keeps a day's answer once chosen (Redis shares it), so a pool change mid-day doesn't move it.</item>
/// <item><b>Stateless, on trust:</b> the client says which attempt a guess is, and sees the answer after the last, so
/// someone can skip to it; it's a game, and the answer is only a show title. Progress and streaks live on the device.</item>
/// <item><b>Days:</b> the client sends its local date; yesterday, today and tomorrow by UTC are accepted, which covers
/// every time zone.</item>
/// </list>
/// </summary>
public static class PuzzleApi
{
    public const int MaxGuesses = 6;
    public const int CloseYears = 3;
    public static readonly DateOnly FirstDay = new(2026, 10, 29);

    private const double MinPopularity = 99;
    private const double MinRating = 7.5;
    private const int NoRepeatDays = 30;

    public static void MapPuzzleApi(this RouteGroupBuilder api)
    {
        api.MapGet("/puzzles/{date}", GetAsync).RequireFeature(FeatureFlags.ShowPuzzle);
        api.MapPost("/puzzles/{date}/guesses", GuessAsync).RequireFeature(FeatureFlags.ShowPuzzle).RequireRateLimiting(SearchRateLimiting.Policy);
    }

    private static async Task<Results<Ok<PuzzleDto>, NotFound>> GetAsync(DateOnly date, CatalogContext db, HybridCache cache, TimeProvider time,
        CancellationToken ct)
    {
        if (!Playable(date, time) || await AnswerAsync(date, db, cache, time.GetUtcNow(), ct) is not { } answer)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(new PuzzleDto(NumberOf(date), date, MaxGuesses, CluesFor(answer, 0)));
    }

    private static async Task<Results<Ok<PuzzleGuessDto>, NotFound, ValidationProblem>> GuessAsync(DateOnly date, PuzzleGuessRequest request,
        CatalogContext db, HybridCache cache, TimeProvider time, CancellationToken ct)
    {
        if (request.Attempt is < 1 or > MaxGuesses)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["attempt"] = [$"Attempt must be 1 to {MaxGuesses}."] });
        }
        if (!Playable(date, time) || await AnswerAsync(date, db, cache, time.GetUtcNow(), ct) is not { } answer
            || await LoadAsync(db, [request.ShowId], time.GetUtcNow(), ct) is not [var guess])
        {
            return TypedResults.NotFound();
        }

        var correct = guess.Id == answer.Id;
        var over = correct || request.Attempt == MaxGuesses;
        return TypedResults.Ok(new PuzzleGuessDto(correct, Compare(guess, answer), CluesFor(answer, correct ? request.Attempt - 1 : request.Attempt),
            over ? answer.Show : null));
    }

    private static bool Playable(DateOnly date, TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return date >= FirstDay && Math.Abs(date.DayNumber - today.DayNumber) <= 1;
    }

    private static int NumberOf(DateOnly date) => date.DayNumber - FirstDay.DayNumber + 1;

    /// <summary>What a show is, as the puzzle compares it.</summary>
    private sealed record PuzzleShow(Guid Id, string Title, string? PosterUrl, int? Year, string? Network, IReadOnlyList<string> Genres,
        string? Kind, int Seasons, ShowStatus Status, IReadOnlyList<(string Name, string? Character)> Cast)
    {
        public PuzzleShowDto Show => new(Id, Title, PosterUrl);
    }

    private static async Task<PuzzleShow?> AnswerAsync(DateOnly date, CatalogContext db, HybridCache cache, DateTimeOffset now, CancellationToken ct)
    {
        // Kept for the day once chosen, so a pool change during the day doesn't change the answer under a player.
        var id = await cache.GetOrCreateAsync($"puzzle-answer:{date:yyyy-MM-dd}", async token =>
        {
            var pool = await cache.GetOrCreateAsync("puzzle-pool", async t => await PoolAsync(db, t),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(6), LocalCacheExpiration = TimeSpan.FromHours(6) }, cancellationToken: token);
            if (pool.Count <= NoRepeatDays)
            {
                return (Guid?)null;
            }
            // The last 30 days' first choices are skipped, so a show doesn't come back within a month.
            var recent = Enumerable.Range(1, NoRepeatDays).Select(d => pool.MinBy(id => Rank(date.AddDays(-d), id))).ToHashSet();
            return pool.Where(id => !recent.Contains(id)).MinBy(id => Rank(date, id));
        }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(2), LocalCacheExpiration = TimeSpan.FromDays(2) }, cancellationToken: ct);
        return id is { } answerId && await LoadAsync(db, [answerId], now, ct) is [var answer] ? answer : null;
    }

    private static ulong Rank(DateOnly date, Guid id) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"puzzle:{date:yyyy-MM-dd}:{id}")));

    /// <summary>
    /// Popular, well-rated English-language scripted and animated shows with a dated premiere and two credited characters:
    /// shows people can name. With popularity 95 and rating 7 (437 shows), a sample of 30 included Tehran and Maigret.
    /// </summary>
    private static async Task<List<Guid>> PoolAsync(CatalogContext db, CancellationToken ct) =>
        await db.Shows.AsNoTracking()
            .Where(s => s.Popularity >= MinPopularity && s.ExternalRating >= MinRating && s.FirstAirDate != null
                && s.Keywords.Contains("english") && s.Keywords.Any(k => ShowRules.StoryKinds.Contains(k))
                && s.Cast.Count(c => c.Character != null) >= 2)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

    private static async Task<List<PuzzleShow>> LoadAsync(CatalogContext db, IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken ct)
    {
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id, s.Title, s.PosterUrl, s.FirstAirDate, s.Network, s.Genres, s.Keywords, s.Status,
                Seasons = s.Episodes.Where(e => e.SeasonNumber > 0 && e.AirsAtUtc <= now).Select(e => e.SeasonNumber).Distinct().Count(),
                Cast = s.Cast.OrderBy(c => c.Order).Take(8).Select(c => new { c.Person.Name, c.Character }).ToList()
            })
            .ToListAsync(ct);
        return shows.Select(s => new PuzzleShow(s.Id, s.Title, s.PosterUrl, s.FirstAirDate?.Year, s.Network, s.Genres, ShowKinds.Of(s.Keywords),
            s.Seasons, s.Status, s.Cast.Select(c => (c.Name, c.Character)).ToList())).ToList();
    }

    private static PuzzleFeedbackDto Compare(PuzzleShow guess, PuzzleShow answer)
    {
        var network = guess.Network is not null && string.Equals(guess.Network, answer.Network, StringComparison.OrdinalIgnoreCase) ? "same"
            : NetworkServices.ForNetwork(guess.Network) is { } a && NetworkServices.ForNetwork(answer.Network) is { } b && a.Key == b.Key ? "service"
            : "different";
        var answerCast = answer.Cast.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PuzzleFeedbackDto(
            guess.Show,
            guess.Year,
            Relation(answer.Year, guess.Year, "later", "earlier"),
            guess.Year is { } gy && answer.Year is { } ay && Math.Abs(gy - ay) <= CloseYears,
            guess.Network,
            network,
            guess.Genres.Select(g => new PuzzleGenreDto(g, answer.Genres.Contains(g))).ToList(),
            guess.Kind,
            guess.Kind == answer.Kind,
            guess.Seasons,
            Relation(answer.Seasons, guess.Seasons, "more", "fewer"),
            Math.Abs(guess.Seasons - answer.Seasons) <= 1,
            guess.Status == ShowStatus.Ended ? "ended" : "running",
            (guess.Status == ShowStatus.Ended) == (answer.Status == ShowStatus.Ended),
            guess.Cast.Select(c => c.Name).Where(answerCast.Contains).Distinct().ToList());
    }

    /// <summary>"same", <paramref name="above"/> when the answer's value is the larger, else <paramref name="below"/>.</summary>
    private static string Relation(int? answer, int? guess, string above, string below) =>
        answer is not { } a || guess is not { } g ? "unknown" : a == g ? "same" : a > g ? above : below;

    /// <summary>The clues unlocked after <paramref name="wrong"/> wrong guesses. Names that give the title away are skipped.</summary>
    private static List<PuzzleClueDto> CluesFor(PuzzleShow answer, int wrong)
    {
        var titleWords = Words(answer.Title);
        bool Safe(string? text) => !string.IsNullOrWhiteSpace(text) && !Words(text).Overlaps(titleWords);
        bool Role(string? character) => Safe(character) && !character!.Contains("Himself") && !character.Contains("Herself");
        // The lead is the first-billed actor, named without their character when it gives the title away (Meredith Grey).
        var lead = answer.Cast.FirstOrDefault(c => Safe(c.Name));

        var clues = new List<PuzzleClueDto>
        {
            new("kind", 0, answer.Kind == "animation" ? "An animated show" : "A scripted series")
        };
        if (answer.Year is { } year)
        {
            clues.Add(new("decade", 2, $"It premiered in the {year / 10 * 10}s"));
        }
        if (answer.Network is { } network && Safe(network))
        {
            clues.Add(new("network", 3, $"It's on {network}"));
        }
        if (answer.Cast.FirstOrDefault(c => c != lead && Role(c.Character)) is { Character: { } character })
        {
            clues.Add(new("character", 4, $"One of its characters: {character}"));
        }
        if (lead != default)
        {
            clues.Add(new("lead", 5, $"It stars {lead.Name}{(Role(lead.Character) ? $" as {lead.Character}" : "")}"));
        }
        return clues.Where(c => c.AfterWrong <= wrong).ToList();
    }

    /// <summary>A text's words of four letters or more, for checking a clue doesn't contain the title.</summary>
    private static HashSet<string> Words(string text) =>
        text.Split([' ', ':', '-', '\'', '.', ',', '!', '?', '&', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4).Select(w => w.ToLowerInvariant()).ToHashSet();
}

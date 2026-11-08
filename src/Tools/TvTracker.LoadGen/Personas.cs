using System.Text.RegularExpressions;

namespace TvTracker.LoadGen;

/// <summary>
/// Ten fixed people to test the app as (README, Test personas): each signs in as <c>persona-{key}</c> and is shaped to
/// show a different part of the app, from a long imported history to an empty account, a paying family to a private
/// profile. Unlike <c>seed</c>'s random users, who they are never changes; only their watch dates move with the day it
/// runs.
/// </summary>
/// <param name="Key">The username's suffix.</param>
/// <param name="Shows">How many shows, at least and at most.</param>
/// <param name="Months">How far back the history goes.</param>
/// <param name="Airing">Only running shows with an episode in the last three weeks.</param>
/// <param name="QuietDays">Nothing watched in the last this many days.</param>
/// <param name="Plan">A plan the app's admin sets: Plus, or Family for the family's owner.</param>
/// <param name="FamilyOf">Joins this persona's family (who must own one).</param>
/// <param name="Private">A private profile: follows are requests, approved only for <paramref name="Approves"/>.</param>
/// <param name="Follows">The personas they follow.</param>
internal sealed record TestPersona(string Key, string Name, string Zone, string Taste, string[] Genres, (int Min, int Max) Shows,
    int Months, Pace Pace = Pace.Evenings, Mix? Mix = null, bool Airing = false, int QuietDays = 0, double CommentChance = 0.15,
    int MaxComments = 4, string? Plan = null, string? FamilyOf = null, bool Private = false, string[]? Approves = null,
    string[]? Follows = null)
{
    public const string Prefix = "persona-";

    public string Username => Prefix + Key;

    public string FirstName => Name.Split(' ')[0];

    public string LastName => Name.Split(' ')[^1];

    /// <summary>
    /// The longest history, which a show's episodes are loaded for.
    /// </summary>
    public static int MaxMonths => All.Max(p => p.Months);

    // What each is for is in the README's table; a persona's comment says the shape that makes it so.
    public static readonly IReadOnlyList<TestPersona> All =
    [
        // The TV Time refugee: years of dated history, half of it finished.
        new("tess", "Tess Harper", "Europe/London", "drama and romance", ["Drama", "Romance", "Comedy", "Mystery"], (18, 22), 30,
            Mix: new(0.1, 0.5, 0.25), Follows: ["marco", "priya", "mira"]),
        // Keeps up: running shows only, each new episode within a day of airing.
        new("marco", "Marco Bellini", "America/New_York", "crime on air now", ["Crime", "Thriller", "Drama", "Action"], (5, 7), 6,
            Pace.KeepsUp, new(0, 0, 1), Airing: true, Follows: ["tess", "sam"]),
        // The binge watcher: whole seasons in a few late nights, mostly finished.
        new("priya", "Priya Nair", "Asia/Kolkata", "science fiction and fantasy", ["Science-Fiction", "Fantasy", "Supernatural", "Horror"], (8, 12), 6,
            Pace.Binges, new(0.1, 0.7, 0.15), Follows: ["tess", "kenji"]),
        // The lapsed viewer: nothing for seven months, shows stopped part way, a planned backlog.
        new("walt", "Walt Brennan", "America/Chicago", "period drama", ["Drama", "History", "War", "Western"], (10, 14), 24,
            Mix: new(0.3, 0.1, 0.35), QuietDays: 210, Follows: ["tess"]),
        // The newcomer: an account and nothing else.
        new("nia", "Nia Dlamini", "Africa/Johannesburg", "nothing yet", [], (0, 0), 1),
        // Plus, a day ahead of UTC.
        new("kenji", "Kenji Watanabe", "Australia/Sydney", "anime", ["Anime", "Fantasy", "Adventure", "Comedy"], (8, 12), 12,
            Plan: "plus", Follows: ["priya"]),
        // Owns a Family plan.
        new("rosa", "Rosa Alvarez", "America/Los_Angeles", "comedy and family", ["Comedy", "Family", "Romance", "Food"], (10, 14), 12,
            Plan: "family", Follows: ["leo", "tess"]),
        // Plus through Rosa's family.
        new("leo", "Leo Alvarez", "America/Los_Angeles", "comedy and science fiction", ["Comedy", "Science-Fiction", "Adventure", "Family"], (8, 12), 12,
            FamilyOf: "rosa", Follows: ["rosa", "kenji"]),
        // A moderator (Moderation:Users in the AppHost's development settings), with plenty of comments to moderate.
        new("mira", "Mira Novak", "Europe/Berlin", "crime and legal", ["Crime", "Mystery", "Legal", "Drama"], (10, 14), 12,
            CommentChance: 0.5, MaxComments: 10, Follows: ["sam", "tess"]),
        // A private profile: Marco's request approved, Mira's left waiting.
        new("sam", "Sam Okafor", "America/Toronto", "legal and medical drama", ["Drama", "Legal", "Medical", "Thriller"], (8, 12), 12,
            Private: true, Approves: ["marco"], Follows: ["marco"])
    ];
}

public sealed partial class Seeder
{
    /// <summary>
    /// Seeds the personas: each one's history once (the display name marks them done, as <c>seed</c> does), then, on every
    /// run, what makes them who they are: plans (as the app's admin), the family, privacy and follows, all idempotent.
    /// </summary>
    /// <param name="appAdmin">The app's own admin (in Admin:Users, not Keycloak's console admin), who sets plans.</param>
    internal async Task<int> SeedPersonasAsync(IReadOnlyList<TestPersona> personas, (string Username, string Password) appAdmin,
        CancellationToken ct)
    {
        var users = personas.ToDictionary(p => p.Key, p => new Session(keycloak, p.Username, options.Password, options.BffUrl));
        var catalog = await CatalogAsync(users[personas[0].Key], personas.SelectMany(p => p.Genres), ct);

        var unfinished = 0;
        foreach (var (persona, i) in personas.Select((p, i) => (p, i)))
        {
            var (outcome, summary) = await SeedPersonaAsync(users[persona.Key], persona, i, catalog, ct);
            Console.WriteLine($"{persona.Username}: {(outcome == Outcome.Skipped ? "history already seeded" : summary)}" +
                $"{(outcome == Outcome.Unfinished ? " (not finished: run again to finish)" : "")}");
            unfinished += outcome == Outcome.Unfinished ? 1 : 0;
        }

        var ids = (await keycloak.FindUsersAsync(await adminToken(ct), TestPersona.Prefix, ct)).ToDictionary(u => u.Username, u => u.Id);
        string IdOf(string key) => ids[TestPersona.Prefix + key];
        var failures = 0;
        async Task WriteAsync(Session user, HttpMethod method, string path, object? body, int? quiet = null)
        {
            var code = await SendAsync(user, method, path, body, ct, quiet);
            failures += Ok(code) || code == quiet ? 0 : 1;
        }

        // Plans before the family: a family needs its owner on the Family plan.
        var admin = new Session(keycloak, appAdmin.Username, appAdmin.Password, options.BffUrl);
        foreach (var persona in personas.Where(p => p.Plan is not null))
        {
            await WriteAsync(admin, HttpMethod.Put, $"/identity/api/v1/admin/plans/users/{IdOf(persona.Key)}", new { plan = persona.Plan });
        }
        foreach (var member in personas.Where(p => p.FamilyOf is not null))
        {
            var owner = users[member.FamilyOf!];
            await WriteAsync(owner, HttpMethod.Post, "/identity/api/v1/me/family/invite", null);
            using var family = await GetJsonAsync(owner, "/identity/api/v1/me/family", ct);
            if (family?.RootElement.GetProperty("inviteToken").GetString() is { } token)
            {
                await WriteAsync(users[member.Key], HttpMethod.Post, "/identity/api/v1/family/join", new { token });
            }
            else
            {
                Console.Error.WriteLine($"  {member.FamilyOf} has no invite link; is the plans flag on and their plan Family?");
                failures++;
            }
        }
        foreach (var persona in personas.Where(p => p.Private))
        {
            await WriteAsync(users[persona.Key], HttpMethod.Put, "/social/api/v1/me/social/settings", new { shareActivity = true, @private = true });
        }
        foreach (var persona in personas)
        {
            foreach (var key in persona.Follows ?? [])
            {
                await WriteAsync(users[persona.Key], HttpMethod.Post, $"/social/api/v1/people/{IdOf(key)}/follow", null);
            }
        }
        foreach (var persona in personas)
        {
            foreach (var key in persona.Approves ?? [])
            {
                // 404 once approved: there's no request left.
                await WriteAsync(users[persona.Key], HttpMethod.Post, $"/social/api/v1/me/follow-requests/{IdOf(key)}/approve", null, quiet: 404);
            }
        }

        Console.WriteLine($"Seeded {personas.Count} personas{(unfinished > 0 ? $", {unfinished} not finished" : "")}" +
            $"{(failures > 0 ? $", {failures} setup steps failed" : "")}. Sign in as persona-<name> with the --password given.");
        return unfinished > 0 || failures > 0 ? 1 : 0;
    }

    private async Task<(Outcome Outcome, string? Summary)> SeedPersonaAsync(Session user, TestPersona persona, int index, List<Show> catalog,
        CancellationToken ct)
    {
        if (await SeedToUseAsync(user, Regex.Escape(persona.Name), persona.Zone, ct) is not { } seed)
        {
            return (Outcome.Skipped, null);
        }
        var profile = new Profile(persona.Name, persona.Taste, TimeZoneInfo.FindSystemTimeZoneById(persona.Zone), persona.Genres,
            persona.Shows.Min, persona.Shows.Max, persona.Months, persona.Pace, persona.Mix ?? Mix.Usual, persona.Airing,
            persona.QuietDays, persona.CommentChance, persona.MaxComments);
        return await SeedProfileAsync(user, profile, seed, new Random(seed * 7919 + 1000 + index), catalog, ct);
    }
}

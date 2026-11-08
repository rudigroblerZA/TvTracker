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
/// <param name="Plan">A plan an admin sets: Plus, or Family for the family's owner.</param>
/// <param name="FamilyOf">Joins this persona's family (who must own one).</param>
/// <param name="Private">A private profile: follows are requests, approved only for <paramref name="Approves"/>.</param>
/// <param name="Follows">The personas they follow.</param>
/// <param name="Tests">What to look at as them.</param>
public sealed record TestPersona(string Key, string Name, string Zone, string Taste, string[] Genres, (int Min, int Max) Shows,
    int Months, string Pace, string Tests, string? Plan = null, string? FamilyOf = null, bool Private = false,
    string[]? Approves = null, string[]? Follows = null, bool Moderator = false)
{
    public const string Prefix = "persona-";

    public string Username => Prefix + Key;

    public static readonly IReadOnlyList<TestPersona> All =
    [
        new("tess", "Tess Harper", "Europe/London", "drama and romance", ["Drama", "Romance", "Comedy", "Mystery"], (18, 22), 30, "evenings",
            "The TV Time refugee: two and a half years of dated history across 20 shows, half finished. Year in TV, Your TV clock, " +
            "Your TV life, taste twins, Your taste, For you.",
            Follows: ["marco", "priya", "mira"]),
        new("marco", "Marco Bellini", "America/New_York", "crime on air now", ["Crime", "Thriller", "Drama", "Action"], (5, 7), 6, "keeps-up",
            "Keeps up: running shows only, each new episode within a day of airing. Streaks (kept up), Spoiler watch, the waiting " +
            "room, Up Next, the calendar, season verdicts.",
            Follows: ["tess", "sam"]),
        new("priya", "Priya Nair", "Asia/Kolkata", "science fiction and fantasy", ["Science-Fiction", "Fantasy", "Supernatural", "Horror"], (8, 12), 6, "binges",
            "The binge watcher: whole seasons in a few late nights, mostly finished. Your TV clock (night owl, binges), Fits your " +
            "time off, While you wait, Will you finish it?",
            Follows: ["tess", "kenji"]),
        new("walt", "Walt Brennan", "America/Chicago", "period drama", ["Drama", "History", "War", "Western"], (10, 14), 24, "lapsed",
            "The lapsed viewer: nothing watched for seven months, shows stopped part way and a planned backlog. Previously on…, " +
            "Is it worth going back?, Backlog triage, Will you finish it? (stalled).",
            Follows: ["tess"]),
        new("nia", "Nia Dlamini", "Africa/Johannesburg", "nothing yet", [], (0, 0), 1, "none",
            "The newcomer: an account and nothing else. Every empty state, the first follow, Bring your history."),
        new("kenji", "Kenji Watanabe", "Australia/Sydney", "anime", ["Anime", "Fantasy", "Adventure", "Comedy"], (8, 12), 12, "evenings",
            "Plus subscriber, a day ahead of UTC: every Plus feature unlocked (Essential cut on long anime, Tonight's plan, the " +
            "reunion radar, Mixtape), and dates near midnight.",
            Plan: "plus", Follows: ["priya"]),
        new("rosa", "Rosa Alvarez", "America/Los_Angeles", "comedy and family", ["Comedy", "Family", "Romance", "Food"], (10, 14), 12, "evenings",
            "Family plan owner: the plan page's family section (invite link, members), Plus features, Tonight for two with Leo.",
            Plan: "family", Follows: ["leo", "tess"]),
        new("leo", "Leo Alvarez", "America/Los_Angeles", "comedy and science fiction", ["Comedy", "Science-Fiction", "Adventure", "Family"], (8, 12), 12, "evenings",
            "Family member: Plus through Rosa's family without paying, the member's view of the plan page, Tonight for two with Rosa.",
            FamilyOf: "rosa", Follows: ["rosa", "kenji"]),
        new("mira", "Mira Novak", "Europe/Berlin", "crime and legal", ["Crime", "Mystery", "Legal", "Drama"], (10, 14), 12, "evenings",
            "Moderator (Moderation:Users in development): the moderation page, Research and episode notes, and plenty of comments " +
            "to moderate. Her follow request to Sam stays pending.",
            Moderator: true, Follows: ["sam", "tess"]),
        new("sam", "Sam Okafor", "America/Toronto", "legal and medical drama", ["Drama", "Legal", "Medical", "Thriller"], (8, 12), 12, "evenings",
            "Private profile: follow requests (Marco's approved, Mira's waiting), what others see of a private profile, Visibility.",
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
    public async Task<int> SeedPersonasAsync(IReadOnlyList<TestPersona> personas, (string Username, string Password) appAdmin,
        CancellationToken ct)
    {
        var users = personas.ToDictionary(p => p.Key, p => new Session(keycloak, p.Username, options.Password, options.BffUrl));
        historyMonths = Math.Max(options.Months, personas.Max(p => p.Months));
        var catalog = await CatalogAsync(users[personas[0].Key], personas.SelectMany(p => p.Genres), ct);
        if (catalog.Count < 20)
        {
            throw new CliException($"Only {catalog.Count} shows came back from the catalog through {options.BffUrl}; import more first.");
        }

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
            var code = await SendAsync(admin, HttpMethod.Put, $"/identity/api/v1/admin/plans/users/{IdOf(persona.Key)}", new { plan = persona.Plan }, ct);
            failures += Ok(code) ? 0 : 1;
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
        var seed = options.Seed;
        using (var profileJson = await GetJsonAsync(user, "/identity/api/v1/profile", ct))
        {
            var current = profileJson?.RootElement.GetProperty("displayName").GetString() ?? "";
            var state = Regex.Match(current, $"^{Regex.Escape(persona.Name)}(?: \\(seeding, seed (-?\\d+)\\))?$");
            if (state.Success && !state.Groups[1].Success)
            {
                return (Outcome.Skipped, null);
            }
            if (state.Success)
            {
                seed = int.Parse(state.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var (pace, mix, airing, quiet) = persona.Pace switch
        {
            "keeps-up" => (Pace.KeepsUp, new Mix(0, 0, 1), true, 0),
            "binges" => (Pace.Binges, new Mix(0.1, 0.7, 0.15), false, 0),
            "lapsed" => (Pace.Evenings, new Mix(0.3, 0.1, 0.35), false, 210),
            _ => (Pace.Evenings, persona.Key == "tess" ? new Mix(0.1, 0.5, 0.25) : Usual, false, 0)
        };
        var profile = new Profile(persona.Name, persona.Taste, TimeZoneInfo.FindSystemTimeZoneById(persona.Zone), persona.Genres,
            persona.Shows.Min, persona.Shows.Max, persona.Months, pace, mix, airing, quiet,
            CommentChance: persona.Moderator ? 0.5 : 0.15, MaxComments: persona.Moderator ? 10 : 4);
        return await SeedProfileAsync(user, profile, seed, new Random(seed * 7919 + 1000 + index), catalog, ct);
    }
}

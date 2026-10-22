using Catalog.Data;
using TvTracker.IntegrationEvents;

namespace Catalog.SyncWorker.Providers;

/// <summary>
/// Fake catalog for offline development (set Catalog:Provider to "mock"). All shows, people and services are invented.
/// Air times are relative to the start of the current week (Monday 00:00 UTC), so there are always
/// episodes that aired recently and episodes airing in the coming days. The anchor only moves once a
/// week, so refreshes don't report spurious air-time changes.
/// </summary>
public sealed class MockCatalogProvider(TimeProvider timeProvider) : ICatalogProvider
{
    public string Name => "mock";

    public Task<IReadOnlyList<string>> GetFeaturedShowIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(BuildCatalog().Select(s => s.ExternalId).ToList());

    /// <summary>The invented shows have no crew, so there are no careers to look up.</summary>
    public Task<ProviderCareer?> GetCareerAsync(string personExternalId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProviderCareer?>(null);

    public Task<IReadOnlyList<ProviderActingRole>?> GetCastCreditsAsync(string personExternalId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderActingRole>?>(null);

    public Task<IReadOnlyList<string>> GetPopularShowIdsAsync(int count, Action<int>? onPage = null, CancellationToken cancellationToken = default)
    {
        onPage?.Invoke(1);
        return Task.FromResult<IReadOnlyList<string>>(BuildCatalog()
            .OrderByDescending(s => s.Popularity)
            .Take(count)
            .Select(s => s.ExternalId)
            .ToList());
    }

    public Task<IReadOnlyList<string>> SearchShowIdsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(BuildCatalog()
            .Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ExternalId)
            .Take(limit)
            .ToList());

    public Task<ProviderShow?> GetShowAsync(string externalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(BuildCatalog().FirstOrDefault(s => s.ExternalId == externalId));

    private IReadOnlyList<ProviderShow> BuildCatalog()
    {
        var now = timeProvider.GetUtcNow();
        var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        var anchor = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-daysSinceMonday);

        return
        [
            Build(anchor, now, "mock-lighthouse", "The Lighthouse Keepers",
                "Three siblings inherit a remote lighthouse and the secrets buried beneath it.",
                ShowStatus.Airing, "Northwind", 2025, 92.5,
                genres: ["Drama", "Mystery"], keywords: ["family", "island", "secrets", "coastal"],
                seasons: [new(1, 8, -400, 20, 52), new(2, 10, -35, 20, 55)],
                cast: [("Mara Ellison", "Ruth Calder"), ("Tomas Reyes", "Ben Calder"), ("Ada Okafor", "Iris Calder"), ("Glen Whitcombe", "Harbourmaster Doyle")],
                similar: ["mock-hollow", "mock-harbour", "mock-tides"],
                watch: [("US", "Northwind+", WatchProviderType.Stream), ("GB", "Northwind+", WatchProviderType.Stream), ("ZA", "StreamFlix", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-orbit", "Orbit Street",
                "Life, love and lease disputes in the only apartment block on a lunar colony.",
                ShowStatus.Airing, "Comet TV", 2026, 81.2,
                genres: ["Comedy", "Sci-Fi"], keywords: ["space", "neighbours", "workplace", "sitcom"],
                seasons: [new(1, 10, -14, 1, 24)],
                cast: [("Priya Natarajan", "Commander Dee"), ("Leo Brandt", "Gus"), ("Sam Okoro", "Twelve")],
                similar: ["mock-byte", "mock-precinct"],
                watch: [("US", "Comet+", WatchProviderType.Stream), ("GB", "PrimeView", WatchProviderType.Buy), ("ZA", "PrimeView", WatchProviderType.Buy)]),

            Build(anchor, now, "mock-harbour", "Cold Harbour",
                "A disgraced detective returns to the fishing town where her career collapsed.",
                ShowStatus.Ended, "Northwind", 2023, 64.0,
                genres: ["Crime", "Thriller"], keywords: ["detective", "coastal", "small town", "murder"],
                seasons: [new(1, 6, -800, 21, 58), new(2, 6, -430, 21, 58)],
                cast: [("Hanna Lindqvist", "DI Carys Bell"), ("Owen Price", "Tam Rhys"), ("Mira Solberg", "Elin Bell")],
                similar: ["mock-lighthouse", "mock-precinct", "mock-hollow"],
                watch: [("US", "Northwind+", WatchProviderType.Stream), ("GB", "FreeTV", WatchProviderType.Free), ("ZA", "StreamFlix", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-frontiers", "Wild Frontiers",
                "Filmmakers spend a year in the planet's least explored places.",
                ShowStatus.Airing, "Terra", 2024, 58.3,
                genres: ["Documentary"], keywords: ["nature", "wildlife", "travel", "planet"],
                seasons: [new(1, 6, -500, 19, 50), new(2, 6, -150, 19, 50), new(3, 6, 3, 19, 50)],
                cast: [("Dr. Imani Achebe", "Narrator")],
                similar: ["mock-deepfield"],
                watch: [("US", "Terra Go", WatchProviderType.Stream), ("GB", "Terra Go", WatchProviderType.Stream), ("ZA", "Terra Go", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-signal", "Signal Lost",
                "When a deep-space probe starts answering back, a small team must decide who to tell.",
                ShowStatus.Renewed, "Comet TV", 2026, 88.9,
                genres: ["Sci-Fi", "Thriller"], keywords: ["space", "conspiracy", "first contact", "science"],
                seasons: [new(1, 8, -63, 2, 47), new(2, 0, 45, 2, 47)],
                cast: [("Jonah Weller", "Dr. Felix Amari"), ("Rosa Lindgren", "Kit Haldane"), ("Ade Bankole", "Director Moss")],
                similar: ["mock-deepfield", "mock-orbit", "mock-crown"],
                watch: [("US", "Comet+", WatchProviderType.Stream), ("GB", "StreamFlix", WatchProviderType.Stream), ("ZA", "StreamFlix", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-kitchen", "Midnight Kitchen",
                "Home cooks compete overnight in a pop-up restaurant that opens at midnight.",
                ShowStatus.Airing, "Plate", 2022, 47.1,
                genres: ["Reality", "Food"], keywords: ["cooking", "competition", "restaurant"],
                seasons: [new(1, 10, -1200, 18, 44), new(2, 10, -850, 18, 44), new(3, 10, -480, 18, 44), new(4, 12, -21, 18, 44)],
                cast: [("Chef Lorenzo Vitali", "Host"), ("Nomvula Dube", "Judge")],
                similar: ["mock-frontiers"],
                watch: [("US", "FreeTV", WatchProviderType.Free), ("ZA", "Plate Now", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-crown", "The Ashen Crown",
                "Rival heirs fight for a throne that burns whoever sits on it.",
                ShowStatus.Upcoming, "StreamFlix", 2026, 95.4,
                genres: ["Fantasy", "Drama"], keywords: ["kingdom", "magic", "war", "succession"],
                seasons: [new(1, 8, 10, 21, 62)],
                cast: [("Elena Varga", "Queen Ysolde"), ("Marcus Hale", "Prince Corran"), ("Thandi Mokoena", "The Ember Witch"), ("Felix Moreau", "Lord Vane")],
                similar: ["mock-signal", "mock-lighthouse"],
                watch: [("US", "StreamFlix", WatchProviderType.Stream), ("GB", "StreamFlix", WatchProviderType.Stream), ("ZA", "StreamFlix", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-precinct", "Precinct 9",
                "The least ambitious police precinct in the city keeps accidentally solving big cases.",
                ShowStatus.Cancelled, "Comet TV", 2024, 52.7,
                genres: ["Comedy", "Crime"], keywords: ["police", "workplace", "sitcom"],
                seasons: [new(1, 10, -700, 1, 22), new(2, 10, -300, 1, 22)],
                cast: [("Dev Kapoor", "Sgt. Rollo"), ("Bea Hart", "Captain Lin"), ("Moses Adeyemi", "Officer Pike")],
                similar: ["mock-orbit", "mock-byte", "mock-harbour"],
                watch: [("US", "Comet+", WatchProviderType.Stream), ("GB", "PrimeView", WatchProviderType.Rent)]),

            Build(anchor, now, "mock-tides", "Between Tides",
                "Two marine biologists, one research station, and a decade of almosts.",
                ShowStatus.Airing, "Northwind", 2026, 61.8,
                genres: ["Drama", "Romance"], keywords: ["ocean", "science", "love", "coastal"],
                seasons: [new(1, 6, -7, 20, 48)],
                cast: [("Isla Moreno", "Dr. June Avery"), ("Kwame Mensah", "Dr. Theo Lake")],
                similar: ["mock-lighthouse"],
                watch: [("US", "Northwind+", WatchProviderType.Stream), ("ZA", "StreamFlix", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-byte", "Byte Club",
                "A failing start-up's staff will do anything to avoid shipping their product.",
                ShowStatus.Ended, "StreamFlix", 2022, 70.4,
                genres: ["Comedy"], keywords: ["start-up", "technology", "workplace", "sitcom"],
                seasons: [new(1, 8, -900, 23, 28), new(2, 8, -520, 23, 28), new(3, 8, -160, 23, 28)],
                cast: [("Noah Feld", "Max"), ("Zoe Adair", "Priya"), ("Ken Tanaka", "Hiro")],
                similar: ["mock-orbit", "mock-precinct"],
                watch: [("US", "StreamFlix", WatchProviderType.Stream), ("GB", "StreamFlix", WatchProviderType.Stream), ("ZA", "StreamFlix", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-deepfield", "Deep Field",
                "Astronomers explain what the newest telescopes are seeing, week by week.",
                ShowStatus.Airing, "Terra", 2026, 39.6,
                genres: ["Documentary", "Sci-Fi"], keywords: ["space", "science", "astronomy"],
                seasons: [new(1, 5, -1, 3, 45)],
                cast: [("Dr. Sofia Ruiz", "Host")],
                similar: ["mock-frontiers", "mock-signal"],
                watch: [("US", "Terra Go", WatchProviderType.Stream), ("ZA", "Terra Go", WatchProviderType.Stream)]),

            Build(anchor, now, "mock-hollow", "Hollow Pines",
                "A summer camp reopens thirty years after the night that closed it.",
                ShowStatus.Airing, "StreamFlix", 2026, 77.0,
                genres: ["Horror", "Mystery"], keywords: ["small town", "secrets", "teen", "forest"],
                seasons: [new(1, 8, -42, 22, 50)],
                cast: [("Lily Chen", "Mags"), ("Ethan Cole", "Counselor Ray"), ("Grace Mbeki", "Sheriff Ana")],
                similar: ["mock-lighthouse", "mock-harbour"],
                watch: [("US", "StreamFlix", WatchProviderType.Stream), ("GB", "StreamFlix", WatchProviderType.Stream), ("ZA", "StreamFlix", WatchProviderType.Stream)])
        ];
    }

    /// <summary>
    /// A made-up rating that's the same on every run (string.GetHashCode isn't), so refreshes don't
    /// report changes: the show's base rating plus or minus up to 0.7 per episode.
    /// </summary>
    private static double MockRating(double baseRating, string key)
    {
        var hash = key.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return Math.Round(Math.Clamp(baseRating + (Math.Abs(hash % 15) - 7) / 10.0, 1, 10), 1);
    }

    /// <param name="EpisodeCount">0 for a season that has been announced but has no episodes scheduled yet.</param>
    /// <param name="FirstAirsDay">Days from the anchor to the first episode (or premiere); episodes are weekly.</param>
    private record SeasonSpec(int Number, int EpisodeCount, int FirstAirsDay, int HourUtc, int RuntimeMinutes);

    private static ProviderShow Build(
        DateTimeOffset anchor, DateTimeOffset now, string id, string title, string overview, ShowStatus status, string network,
        int startYear, double popularity, string[] genres, string[] keywords, SeasonSpec[] seasons,
        (string Name, string Character)[] cast, string[] similar, (string Region, string Provider, WatchProviderType Type)[] watch)
    {
        var providerSeasons = new List<ProviderSeason>();
        var episodes = new List<ProviderEpisode>();

        // Popularity 39-95 maps to a 6.3-9.2 base rating.
        var baseRating = Math.Round(5 + popularity / 22.5, 1);

        foreach (var season in seasons)
        {
            var premiere = anchor.AddDays(season.FirstAirsDay).AddHours(season.HourUtc);
            providerSeasons.Add(new ProviderSeason(season.Number, $"Season {season.Number}", null, premiere));

            for (var n = 1; n <= season.EpisodeCount; n++)
            {
                episodes.Add(new ProviderEpisode(
                    ExternalId: $"{id}-s{season.Number:00}e{n:00}",
                    SeasonNumber: season.Number,
                    EpisodeNumber: n,
                    Title: n == 1 ? (season.Number == 1 ? "Pilot" : "Return") : $"Chapter {n}",
                    Overview: null,
                    AirsAtUtc: premiere.AddDays(7 * (n - 1)),
                    RuntimeMinutes: season.RuntimeMinutes,
                    // Like a real provider, only aired episodes have ratings.
                    Rating: premiere.AddDays(7 * (n - 1)) <= now ? MockRating(baseRating, $"{id}-s{season.Number}e{n}") : null));
            }
        }

        return new ProviderShow(
            ExternalId: id,
            Title: title,
            Overview: overview,
            PosterUrl: null,
            BackdropUrl: null,
            // startYear is informational; the first air date follows the generated schedule.
            FirstAirDate: providerSeasons.Count > 0
                ? DateOnly.FromDateTime(providerSeasons.Min(s => s.PremieresAtUtc!.Value).UtcDateTime)
                : new DateOnly(startYear, 1, 1),
            Status: status,
            Network: network,
            Genres: genres,
            Keywords: keywords,
            Popularity: popularity,
            Seasons: providerSeasons,
            Episodes: episodes,
            Cast: cast.Select(c => new ProviderCastMember(c.Name, c.Character, null, [])).ToList(),
            WatchOptions: watch.Select(w => new ProviderWatchOption(w.Region, w.Provider, w.Type, null)).ToList(),
            SimilarExternalIds: similar,
            Rating: episodes.Any(e => e.Rating is not null) ? baseRating : null);
    }
}

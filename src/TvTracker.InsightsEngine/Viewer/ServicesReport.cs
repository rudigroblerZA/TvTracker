using TvTracker.IntegrationEvents;

namespace TvTracker.InsightsEngine;

/// <param name="Minutes">Watched in the last <see cref="ServicesReport.WindowDays"/> days, bulk marks left out.</param>
/// <param name="CostPerHour">The month's price over those hours; null when the price is unknown or free, or nothing was watched.</param>
/// <param name="Verdict">"value", "pricey", "quiet" (nothing watched, but followed shows are coming), "pause", or "free" /
/// "unpriced" for a service with no price to weigh.</param>
/// <param name="Watched">The shows watched on it in the window, most minutes first.</param>
/// <param name="Coming">Followed shows with episodes airing on it in the next <see cref="ServicesReport.WindowDays"/> days.</param>
public record ServiceUseDto(string Key, string Name, decimal? MonthlyUsd, int Minutes, decimal? CostPerHour, string Verdict,
    IReadOnlyList<ServiceShowDto> Watched, IReadOnlyList<ServiceShowDto> Coming);

/// <param name="Minutes">Watched in the window (for <see cref="ServiceUseDto.Watched"/>).</param>
/// <param name="Episodes">Episodes watched, or airing in the window (for <see cref="ServiceUseDto.Coming"/>).</param>
/// <param name="NextAirsAtUtc">The next episode, for coming shows.</param>
public record ServiceShowDto(Guid ShowId, string Title, int Minutes, int Episodes, DateTimeOffset? NextAirsAtUtc);

/// <param name="MonthlyUsd">The month's total for the services with a known price.</param>
/// <param name="UnknownPrices">Services in the total without a known price.</param>
/// <param name="Missing">Services the caller doesn't have, with followed shows coming on them.</param>
public record ServicesReportDto(int WindowDays, decimal MonthlyUsd, int UnknownPrices, int Minutes, IReadOnlyList<ServiceUseDto> Services,
    IReadOnlyList<ServiceUseDto> Missing, string PricesAsOf);

/// <summary>
/// The streaming services report: what the viewer's services cost against what they watched on them, and what's coming.
/// A show's service is its network's (<see cref="NetworkServices"/>).
/// </summary>
public static class ServicesReport
{
    public const int WindowDays = 30;

    private const decimal PriceyPerHour = 2m;

    private const int MaxShowsListed = 5;

    /// <summary>
    /// What the viewer got from each service they keep over the window, and the services they don't keep that have
    /// something of theirs coming.
    /// </summary>
    /// <param name="owned">The keys of the services they keep.</param>
    /// <param name="watched">Minutes and episodes watched in the window, per show (bulk marks left out).</param>
    /// <param name="coming">Episodes of followed shows airing in the window ahead, per show, with the first of them.</param>
    /// <param name="shows">Those shows' titles and networks.</param>
    internal static ServicesReportDto Of(IReadOnlyCollection<string> owned, IReadOnlyDictionary<Guid, (int Minutes, int Episodes)> watched,
        IReadOnlyDictionary<Guid, (int Episodes, DateTimeOffset? Next)> coming, IReadOnlyDictionary<Guid, (string Title, string? Network)> shows)
    {
        var serviceOf = shows
            .Select(s => (ShowId: s.Key, Service: NetworkServices.ForNetwork(s.Value.Network)))
            .Where(s => s.Service is not null)
            .ToDictionary(s => s.ShowId, s => s.Service!.Key);

        ServiceUseDto Use(NetworkService service)
        {
            var mine = serviceOf.Where(s => s.Value == service.Key).Select(s => s.Key).ToList();
            var watchedHere = mine.Where(watched.ContainsKey)
                .Select(id => new ServiceShowDto(id, shows[id].Title, watched[id].Minutes, watched[id].Episodes, null))
                .OrderByDescending(s => s.Minutes).ToList();
            var comingHere = mine.Where(coming.ContainsKey)
                .Select(id => new ServiceShowDto(id, shows[id].Title, 0, coming[id].Episodes, coming[id].Next))
                .OrderBy(s => s.NextAirsAtUtc).ToList();
            var minutes = watchedHere.Sum(s => s.Minutes);
            decimal? perHour = service.MonthlyUsd is > 0 && minutes > 0 ? Math.Round(service.MonthlyUsd.Value / (minutes / 60m), 2) : null;
            var verdict = service.MonthlyUsd switch
            {
                null => "unpriced",
                0 => "free",
                _ when minutes == 0 => comingHere.Count > 0 ? "quiet" : "pause",
                _ => perHour > PriceyPerHour ? "pricey" : "value"
            };
            return new ServiceUseDto(service.Key, service.Name, service.MonthlyUsd, minutes, perHour, verdict,
                watchedHere.Take(MaxShowsListed).ToList(), comingHere.Take(MaxShowsListed).ToList());
        }

        var mineUse = owned.Select(NetworkServices.Get).OfType<NetworkService>().Select(Use)
            .OrderByDescending(s => s.Minutes).ToList();
        var missing = serviceOf.Values.Distinct().Except(owned)
            .Select(NetworkServices.Get).OfType<NetworkService>().Select(Use)
            .Where(s => s.Coming.Count > 0)
            .OrderByDescending(s => s.Coming.Count).ToList();

        return new ServicesReportDto(
            WindowDays,
            mineUse.Sum(s => s.MonthlyUsd ?? 0),
            mineUse.Count(s => s.MonthlyUsd is null),
            mineUse.Sum(s => s.Minutes),
            mineUse,
            missing,
            NetworkServices.PricesAsOf);
    }
}

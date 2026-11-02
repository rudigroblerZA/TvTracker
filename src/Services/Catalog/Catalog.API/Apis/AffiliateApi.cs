using System.Globalization;
using System.Security.Claims;
using Catalog.API.Services;
using Catalog.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker;

namespace Catalog.API.Apis;

/// <param name="Surface">Where the link was (<see cref="OutboundSurfaces"/>).</param>
public record OutboundRequest(Guid ShowId, string Provider, WatchProviderType Type, string Region, string Surface);

/// <param name="Url">Where to send the viewer: the program's tracked link, or the service's own when it has none.</param>
public record OutboundDto(string Url, Guid ClickId, string? Program);

public record MoneyDto(string Currency, decimal Amount);

/// <param name="Program">Null for clicks on a service with no program: the traffic it would be worth.</param>
/// <param name="EarningsPerClick">Revenue over clicks, in <paramref name="Currency"/>.</param>
public record AffiliateServiceRowDto(string Service, string? Program, int Clicks, int Conversions, decimal Revenue, string? Currency,
    decimal? EarningsPerClick, double? ConversionRate);

public record AffiliateBreakdownDto(string Key, int Clicks, int Conversions);

public record AffiliateDayDto(DateOnly Day, int Clicks, int Conversions);

public record AffiliateShowRowDto(Guid ShowId, string Title, int Clicks, int Conversions);

/// <summary>
/// A configured program as the report shows it: never its template or key.
/// </summary>
public record AffiliateProgramDto(string Name, IReadOnlyList<string> Services, IReadOnlyList<string> Regions, string Currency, bool Valid);

/// <param name="Revenue">Commission reported in the window, per currency.</param>
public record AffiliateReportDto(DateOnly From, DateOnly To, int Clicks, int AffiliatedClicks, int Conversions, IReadOnlyList<MoneyDto> Revenue,
    IReadOnlyList<AffiliateServiceRowDto> Services, IReadOnlyList<AffiliateBreakdownDto> Surfaces, IReadOnlyList<AffiliateBreakdownDto> Plans,
    IReadOnlyList<AffiliateDayDto> Days, IReadOnlyList<AffiliateShowRowDto> Shows, IReadOnlyList<AffiliateProgramDto> Programs);

/// <summary>
/// Affiliate links, measured: where-to-watch clicks go through <c>POST /outbound</c>, which records the click and answers
/// with where to send the viewer; networks report sales to <c>/affiliates/postback/{program}</c>; admins read the report.
/// <list type="bullet">
/// <item><b>Only stored links:</b> the destination is the show's listed link for that provider (the same one the show page
/// shows), wrapped by its program, never an address from the request, so this can't be used as an open redirect.</item>
/// <item><b>Nothing about who:</b> a click keeps the show, service, link, region, surface and plan, no user id, address
/// or browser. Its id is the sub-id a network posts back with a sale; nothing else ties the two to a person.</item>
/// <item><b>Postbacks aren't behind the flag:</b> sales reported after the links are turned off still count.</item>
/// </list>
/// </summary>
public static class AffiliateApi
{
    private const int MaxReportDays = 365;
    private const string SignedOut = "signed-out";
    private const string UnknownPlan = "unknown";

    public static void MapAffiliateApi(this RouteGroupBuilder api)
    {
        api.MapPost("/outbound", OutboundAsync)
            .RequireRateLimiting(SearchRateLimiting.Policy)
            .RequireFeature(FeatureFlags.Affiliates);
        api.MapGet("/affiliates/postback/{program}", PostbackAsync);
        api.MapGet("/affiliates/report", ReportAsync).RequireAuthorization().RequireFeature(FeatureFlags.Affiliates);
    }

    private static async Task<Results<Ok<OutboundDto>, NotFound, ValidationProblem>> OutboundAsync(OutboundRequest request, HttpContext http,
        CatalogContext db, IOptionsMonitor<AffiliateOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!OutboundSurfaces.All.Contains(request.Surface) || request.Region is not { Length: 2 } || request.Provider is not { Length: > 0 and <= 128 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [$"Give a provider, a two-letter region and a surface ({string.Join(", ", OutboundSurfaces.All)})."]
            });
        }

        var region = request.Region.ToUpperInvariant();
        var listing = await db.WatchProviders.AsNoTracking()
            .Where(w => w.ShowId == request.ShowId && w.Region == region && w.ProviderName == request.Provider && w.Type == request.Type)
            .Join(db.Shows, w => w.ShowId, s => s.Id, (w, s) => new { Provider = w, s.Title })
            .FirstOrDefaultAsync(ct);
        if (listing is null || CatalogApi.WatchLinkFor(listing.Provider, listing.Title) is not { Url: { } url, Link: { } link })
        {
            return TypedResults.NotFound();
        }

        var click = new OutboundClick
        {
            Id = Guid.CreateVersion7(),
            ShowId = request.ShowId,
            Service = listing.Provider.ProviderName,
            Type = request.Type,
            Link = link,
            Region = region,
            Surface = request.Surface,
            Plan = await PlanAsync(http),
            ClickedAtUtc = time.GetUtcNow().UtcDateTime
        };
        var program = AffiliateLinks.For(options.CurrentValue, click.Service, region);
        click.Program = program?.Name;
        db.OutboundClicks.Add(click);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new OutboundDto(program is null ? url : AffiliateLinks.Wrap(program, url, click.Id), click.Id, program?.Name));
    }

    /// <summary>
    /// A network's server-to-server postback: <c>?click={click id}&amp;transaction={its id}&amp;amount={commission}&amp;key={secret}</c>.
    /// Unknown programs, wrong keys and clicks that didn't go through this program all get 404, so the address says
    /// nothing to someone guessing; a repeated transaction is answered as already counted.
    /// </summary>
    private static async Task<Results<Ok<string>, NotFound, ValidationProblem>> PostbackAsync(string program, string? click, string? transaction,
        string? amount, string? currency, string? key, CatalogContext db, IOptionsMonitor<AffiliateOptions> options, TimeProvider time,
        CancellationToken ct)
    {
        var found = options.CurrentValue.Programs.FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase));
        if (found is null || !AffiliateLinks.KeyMatches(found, key) || !Guid.TryParse(click, out var clickId))
        {
            return TypedResults.NotFound();
        }

        var problem = !decimal.TryParse(amount ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 100_000
                ? "amount is a commission of 0 to 100000, with a dot for decimals."
            : currency is { Length: > 0 } && !string.Equals(currency, found.Currency, StringComparison.OrdinalIgnoreCase)
                ? $"This program is paid in {found.Currency}."
            : transaction is { Length: > 128 } ? "transaction is at most 128 characters."
            : null;
        if (problem is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["postback"] = [problem] });
        }

        if (!await db.OutboundClicks.AnyAsync(c => c.Id == clickId && c.Program == found.Name, ct))
        {
            return TypedResults.NotFound();
        }

        var transactionId = transaction is { Length: > 0 } ? transaction : clickId.ToString("N");
        if (await db.AffiliateConversions.AnyAsync(c => c.Program == found.Name && c.TransactionId == transactionId, ct))
        {
            return TypedResults.Ok("already counted");
        }

        db.AffiliateConversions.Add(new AffiliateConversion
        {
            Id = Guid.CreateVersion7(),
            ClickId = clickId,
            Program = found.Name,
            TransactionId = transactionId,
            Amount = Math.Round(value, 2),
            Currency = found.Currency.ToUpperInvariant(),
            ReceivedAtUtc = time.GetUtcNow().UtcDateTime
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same postback twice at once: the unique index let the other one count it.
            db.ChangeTracker.Clear();
            if (await db.AffiliateConversions.AnyAsync(c => c.Program == found.Name && c.TransactionId == transactionId, ct))
            {
                return TypedResults.Ok("already counted");
            }
            throw;
        }
        return TypedResults.Ok("ok");
    }

    /// <summary>
    /// The last <paramref name="days"/> days (UTC), for admins; anyone else gets 404, as with the flags page.
    /// </summary>
    private static async Task<Results<Ok<AffiliateReportDto>, NotFound>> ReportAsync(ClaimsPrincipal user, IConfiguration config, CatalogContext db,
        IOptionsMonitor<AffiliateOptions> options, TimeProvider time, int days = 30, CancellationToken ct = default)
    {
        if (!Admins.Is(user, config))
        {
            return TypedResults.NotFound();
        }

        var to = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-(Math.Clamp(days, 1, MaxReportDays) - 1));
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var clicks = (await db.OutboundClicks.AsNoTracking()
                .Where(c => c.ClickedAtUtc >= start)
                .GroupBy(c => new { c.ClickedAtUtc.Date, c.ShowId, c.Service, c.Program, c.Surface, c.Plan })
                .Select(g => new { g.Key.Date, g.Key.ShowId, g.Key.Service, g.Key.Program, g.Key.Surface, g.Key.Plan, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new ClickGroup(DateOnly.FromDateTime(g.Date), g.ShowId, g.Service, g.Program, g.Surface, g.Plan, g.Count))
            .ToList();
        var conversions = (await db.AffiliateConversions.AsNoTracking()
                .Where(c => c.ReceivedAtUtc >= start)
                .Join(db.OutboundClicks, c => c.ClickId, k => k.Id, (c, k) => new
                {
                    c.ReceivedAtUtc, k.ShowId, k.Service, k.Surface, k.Plan, c.Program, c.Amount, c.Currency
                })
                .ToListAsync(ct))
            .Select(c => new ConversionRow(DateOnly.FromDateTime(c.ReceivedAtUtc), c.ShowId, c.Service, c.Surface, c.Plan, c.Program, c.Amount,
                c.Currency))
            .ToList();
        var showIds = clicks.Select(c => c.ShowId).Concat(conversions.Select(c => c.ShowId)).Distinct().ToList();
        var titles = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        return TypedResults.Ok(AffiliateReport.Build(from, to, clicks, conversions, titles, options.CurrentValue.Programs));
    }

    private static async Task<string> PlanAsync(HttpContext http) =>
        http.User.Identity?.IsAuthenticated != true ? SignedOut
        : http.RequestServices.GetService<IUserPlans>() is { } plans ? await plans.PlanAsync(http) ?? UnknownPlan
        : UnknownPlan;
}

internal sealed record ClickGroup(DateOnly Day, Guid ShowId, string Service, string? Program, string Surface, string Plan, int Clicks);

internal sealed record ConversionRow(DateOnly Day, Guid ShowId, string Service, string Surface, string Plan, string Program, decimal Amount, string Currency);

/// <summary>
/// The affiliate report from grouped clicks and the window's conversions. Conversions count in the window they were
/// reported in (when the money was earned), against the service, surface, plan and show of the click they came from.
/// </summary>
internal static class AffiliateReport
{
    private const int TopShows = 10;

    public static AffiliateReportDto Build(DateOnly from, DateOnly to, IReadOnlyList<ClickGroup> clicks, IReadOnlyList<ConversionRow> conversions,
        IReadOnlyDictionary<Guid, string> titles, IReadOnlyList<AffiliateProgram> programs)
    {
        var currencies = programs.ToDictionary(p => p.Name, p => p.Currency.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

        var services = clicks.Select(c => (c.Service, c.Program)).Concat(conversions.Select(c => (Service: c.Service, Program: (string?)c.Program))).Distinct()
            .Select(key =>
            {
                var clicked = clicks.Where(c => c.Service == key.Service && c.Program == key.Program).Sum(c => c.Clicks);
                var sold = conversions.Where(c => c.Service == key.Service && c.Program == key.Program).ToList();
                var currency = sold.Select(c => c.Currency).FirstOrDefault() ?? (key.Program is { } p ? currencies.GetValueOrDefault(p) : null);
                var revenue = sold.Sum(c => c.Amount);
                return new AffiliateServiceRowDto(key.Service, key.Program, clicked, sold.Count, revenue, currency,
                    key.Program is null || clicked == 0 ? null : Math.Round(revenue / clicked, 4),
                    key.Program is null || clicked == 0 ? null : Math.Round((double)sold.Count / clicked, 4));
            })
            .OrderByDescending(r => r.Revenue).ThenByDescending(r => r.Clicks).ThenBy(r => r.Service, StringComparer.Ordinal)
            .ToList();

        List<AffiliateBreakdownDto> By(Func<ClickGroup, string> clickKey, Func<ConversionRow, string> conversionKey) =>
            clicks.Select(clickKey).Concat(conversions.Select(conversionKey)).Distinct()
                .Select(k => new AffiliateBreakdownDto(k, clicks.Where(c => clickKey(c) == k).Sum(c => c.Clicks), conversions.Count(c => conversionKey(c) == k)))
                .OrderByDescending(b => b.Clicks).ThenBy(b => b.Key, StringComparer.Ordinal)
                .ToList();

        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays)
            .Select(d => new AffiliateDayDto(d, clicks.Where(c => c.Day == d).Sum(c => c.Clicks), conversions.Count(c => c.Day == d)))
            .ToList();

        var shows = clicks.GroupBy(c => c.ShowId)
            .Select(g => new AffiliateShowRowDto(g.Key, titles.GetValueOrDefault(g.Key) ?? "(removed show)", g.Sum(c => c.Clicks),
                conversions.Count(c => c.ShowId == g.Key)))
            .OrderByDescending(s => s.Clicks).ThenByDescending(s => s.Conversions).ThenBy(s => s.Title, StringComparer.Ordinal)
            .Take(TopShows)
            .ToList();

        return new AffiliateReportDto(from, to, clicks.Sum(c => c.Clicks), clicks.Where(c => c.Program is not null).Sum(c => c.Clicks),
            conversions.Count,
            conversions.GroupBy(c => c.Currency).Select(g => new MoneyDto(g.Key, g.Sum(c => c.Amount))).OrderBy(m => m.Currency, StringComparer.Ordinal).ToList(),
            services, By(c => c.Surface, c => c.Surface), By(c => c.Plan, c => c.Plan), days, shows,
            programs.Select(p => new AffiliateProgramDto(p.Name, p.Services, p.Regions, p.Currency.ToUpperInvariant(), AffiliateLinks.IsValid(p))).ToList());
    }
}

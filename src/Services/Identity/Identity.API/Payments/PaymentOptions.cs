using TvTracker.IntegrationEvents;

namespace Identity.API.Payments;

/// <summary>
/// Payments (<c>Payments:*</c>): which provider sells plans, at what prices, and where the buyer comes back to.
/// </summary>
public sealed class PaymentOptions
{
    /// <summary>
    /// The provider new checkouts go to (a registered <see cref="IPaymentProvider.Name"/>); none means plans can't be
    /// bought. Webhooks are accepted from every registered provider, so subscriptions bought before a switch carry on.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Where the buyer lands after a checkout: the web app's plan page (a real provider needs it absolute). Set by the
    /// server, never taken from a request, so a checkout can't be used to send someone to another site.
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// Days a subscription stays active past its paid period while no renewal or ending has arrived (a late or lost
    /// webhook), before <see cref="Services.SubscriptionLapse"/> ends it.
    /// </summary>
    public int GraceDays { get; set; } = 3;

    /// <summary>
    /// Prices by region; <c>*</c> applies everywhere else. Empty means <see cref="PlanPrices.Default"/>.
    /// </summary>
    public List<RegionPrices> Prices { get; set; } = [];

    public TestProviderOptions Test { get; set; } = new();
}

/// <summary>
/// The development stand-in for a provider (<see cref="TestPaymentProvider"/>): its checkout is the web app's
/// <c>/dev/payments</c>, which signs its webhooks with the shared secret.
/// </summary>
public sealed class TestProviderOptions
{
    public string? WebhookSecret { get; set; }
}

/// <param name="Region">A profile region (two letters), or <c>*</c>.</param>
public sealed record RegionPrices(string Region, string Currency, decimal PlusMonth, decimal PlusYear, decimal FamilyMonth, decimal FamilyYear)
{
    public RegionPrices() : this("*", "USD", 0, 0, 0, 0)
    {
    }

    public decimal For(string plan, string interval) => (plan, interval) switch
    {
        (Plans.Plus, PlanPrices.Month) => PlusMonth,
        (Plans.Plus, PlanPrices.Year) => PlusYear,
        (Plans.Family, PlanPrices.Month) => FamilyMonth,
        (Plans.Family, PlanPrices.Year) => FamilyYear,
        _ => throw new ArgumentException($"No price for {plan} by the {interval}.")
    };
}

/// <summary>
/// What a plan costs where. Prices are configuration (<see cref="PaymentOptions.Prices"/>); a real provider's own
/// price list must match them, since the checkout is made for the amount here.
/// </summary>
public static class PlanPrices
{
    public const string Month = "month";
    public const string Year = "year";
    public static readonly string[] Intervals = [Month, Year];

    /// <summary>
    /// Plus $2.99 and Family $4.99 a month, a year for ten months' price.
    /// </summary>
    public static readonly RegionPrices Default = new("*", "USD", 2.99m, 29.99m, 4.99m, 49.99m);

    public static bool Sold(string plan) => plan is Plans.Plus or Plans.Family;

    /// <summary>
    /// The prices for a profile's region: its own row, else <c>*</c>, else <see cref="Default"/>.
    /// </summary>
    public static RegionPrices For(string? region, IReadOnlyList<RegionPrices> configured) =>
        configured.FirstOrDefault(p => string.Equals(p.Region, region, StringComparison.OrdinalIgnoreCase))
        ?? configured.FirstOrDefault(p => p.Region == "*")
        ?? Default;

    /// <summary>
    /// When a period bought now ends.
    /// </summary>
    public static DateTime PeriodEnd(DateTime fromUtc, string interval) => interval == Year ? fromUtc.AddYears(1) : fromUtc.AddMonths(1);
}

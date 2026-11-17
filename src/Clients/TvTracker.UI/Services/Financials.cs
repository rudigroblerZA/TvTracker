namespace TvTracker.UI.Services;

/// <summary>
/// How the financials page words what Financials.API works out.
/// </summary>
public static class Financials
{
    public static readonly int[] Windows = [3, 12, 24];

    /// <summary>
    /// <c>FinancialStatement.Affiliates</c> when affiliate links are switched off, and when Catalog.API couldn't be asked.
    /// </summary>
    public const string AffiliatesOff = "off";
    public const string AffiliatesUnknown = "unknown";

    /// <summary>
    /// An amount with its sign said, for a figure that can be a loss: "+12.00 USD", "−3.50 USD".
    /// </summary>
    public static string Signed(decimal amount, string currency) =>
        $"{(amount < 0 ? "−" : amount > 0 ? "+" : "")}{ReportFormat.Amount(Math.Abs(amount), currency)}";

    public static string CategoryName(string category) => category switch
    {
        "hosting" => "Hosting",
        "data" => "Data licences",
        "services" => "Services",
        "payments" => "Payments",
        "marketing" => "Marketing",
        "legal" => "Legal and accounting",
        "people" => "People",
        _ => "Other"
    };

    public static string RecurrenceName(string recurrence) => recurrence switch
    {
        "yearly" => "a year",
        "once" => "once",
        _ => "a month"
    };
}

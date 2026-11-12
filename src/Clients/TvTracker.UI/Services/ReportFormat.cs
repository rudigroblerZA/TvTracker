namespace TvTracker.UI.Services;

/// <summary>
/// What the admins' report pages (Revenue, Affiliate links) share: the windows on offer and how a bar, a share and a
/// sum of money are shown.
/// </summary>
public static class ReportFormat
{
    /// <summary>
    /// The windows a report can cover, in days.
    /// </summary>
    public static readonly int[] Windows = [7, 30, 90];

    /// <summary>
    /// A bar's width in percent of the longest: nothing for none, and never too thin to see.
    /// </summary>
    public static int Width(int value, int max) => max == 0 || value == 0 ? 0 : Math.Max(2, (int)Math.Round(100.0 * value / max));

    /// <summary>
    /// A part of a whole as a percentage; a dash when there's no whole.
    /// </summary>
    public static string Share(int part, int whole) => whole == 0 ? "–" : ((double)part / whole).ToString("P0");

    public static string Amount(decimal amount, string? currency, int decimals = 2) =>
        $"{amount.ToString($"N{decimals}")} {currency}".Trim();

    /// <summary>
    /// Money per currency, "12.50 USD + 89.00 ZAR"; a dash for none.
    /// </summary>
    public static string Sum(IEnumerable<(decimal Amount, string Currency)> money) =>
        string.Join(" + ", money.Select(m => Amount(m.Amount, m.Currency))) is { Length: > 0 } sum ? sum : "–";
}

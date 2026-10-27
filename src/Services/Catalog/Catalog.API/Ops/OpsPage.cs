using TvTracker;

namespace Catalog.API.Apis;

/// <summary>
/// The ops dashboard page (Ops/ops.html, embedded in the assembly). It loads its data from /ops/stats. "{app}" in the
/// file stands for the app's name.
/// </summary>
internal static class OpsPage
{
    private static readonly string Template = Load();

    public static string Html(Branding brand) => Template.Replace("{app}", System.Net.WebUtility.HtmlEncode(brand.AppName));

    private static string Load()
    {
        using var stream = typeof(OpsPage).Assembly.GetManifestResourceStream("Catalog.API.Ops.ops.html")
            ?? throw new InvalidOperationException("Ops/ops.html isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

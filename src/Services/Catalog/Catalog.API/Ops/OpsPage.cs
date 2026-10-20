namespace Catalog.API.Apis;

/// <summary>The ops dashboard page (Ops/ops.html, embedded in the assembly). It loads its data from /ops/stats.</summary>
internal static class OpsPage
{
    public static readonly string Html = Load();

    private static string Load()
    {
        using var stream = typeof(OpsPage).Assembly.GetManifestResourceStream("Catalog.API.Ops.ops.html")
            ?? throw new InvalidOperationException("Ops/ops.html isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

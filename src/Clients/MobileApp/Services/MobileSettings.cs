using System.Reflection;

namespace TvTracker.Mobile.Services;

/// <summary>
/// Where the app finds the backend, fixed when it is built.
/// <list type="bullet">
/// <item><b>A release</b> names the deployment's host (<c>-p:TvTrackerPublicHost=tv.example.com</c>, the
/// <c>PUBLIC_HOST</c> of scripts/deploy.sh): the site is <c>https://HOST/</c>, the gateway <c>https://HOST/api/</c> and
/// sign-in <c>https://HOST/realms/tvtracker</c>. Nothing goes over plain http.</item>
/// <item><b>A development build</b> names none and talks to the AppHost on this machine, which the Android emulator
/// reaches on 10.0.2.2. A physical phone on the same network reaches it by the machine's LAN address:
/// <c>-p:TvTrackerLocalHost=192.168.1.20</c> (and add that address to the Keycloak client's redirect settings if it
/// changes the issuer). That is still plain http on the AppHost's ports; TvTrackerPublicHost is only for a
/// deployment, which serves https and the gateway under /api.</item>
/// </list>
/// </summary>
public static class MobileSettings
{
    private static readonly IReadOnlyDictionary<string, string> Built = typeof(MobileSettings).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => a.Key.StartsWith("TvTracker.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
        .ToDictionary(a => a.Key, a => a.Value!.Trim());

    /// <summary>
    /// The deployment's host, or null in a development build.
    /// </summary>
    //private static string? PublicHost => Built.GetValueOrDefault("TvTracker.PublicHost");
    private static string? PublicHost => "dev-tvtracker-za.southafricanorth.cloudapp.azure.com";

    /// <summary>
    /// This machine, as the device reaches it: the address built in for a physical phone, or the emulator's.
    /// </summary>
    private static string LocalHost => Built.GetValueOrDefault("TvTracker.LocalHost")
        ?? (DeviceInfo.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost");

    /// <summary>
    /// Whether this build talks to a deployment (over https) and not to this machine, whatever its configuration. The
    /// same question MobileApp.csproj answers with LOCAL_BACKEND, for what has to be decided when it is built.
    /// </summary>
    public static bool TalksToDeployment => PublicHost is not null;

    /// <summary>
    /// The app's name in the UI (white label). The home-screen name is ApplicationTitle in MobileApp.csproj.
    /// </summary>
    public const string AppName = "TV Tracker";

    /// <summary>
    /// Where the backend's catalog comes from (its <c>Catalog:Provider</c>: "tvmaze", "wikidata", "tvdb" or "mock"), for
    /// the credit the source's licence requires in the footer and what the terms and privacy pages name. The web app
    /// reads it from configuration; a phone build can't, so it is built in (<c>-p:TvTrackerCatalogSource=...</c>).
    /// </summary>
    public static string CatalogSource => Built.GetValueOrDefault("TvTracker.CatalogSource") ?? "tvmaze";

    /// <summary>
    /// The mobile gateway: the deployment's, or the Mobile BFF's "http" launch profile (port 5110).
    /// </summary>
    public static Uri BffAddress => PublicHost is { } host ? new($"https://{host}/api/") : new($"http://{LocalHost}:5110/");

    /// <summary>
    /// The web app, for links others open, such as a list's link. In development it's this machine's ("http" launch
    /// profile, port 5120), so a link only opens on it.
    /// </summary>
    public static Uri WebAddress => PublicHost is { } host ? new($"https://{host}/") : new($"http://{LocalHost}:5120/");

    /// <summary>
    /// The Keycloak realm: the deployment's, or the AppHost's (fixed host port 8080).
    /// </summary>
    public static string Authority => PublicHost is { } host ? $"https://{host}/realms/tvtracker" : $"http://{LocalHost}:8080/realms/tvtracker";

    public const string ClientId = "tvtracker-mobile";
    public const string RedirectUri = "tvtracker://callback";
}

namespace TvTracker.Mobile.Services;

/// <summary>
/// Local development addresses. The Android emulator reaches the host machine on 10.0.2.2.
/// For a physical device, replace these with a dev tunnel or your machine's LAN address
/// (and add that address to the Keycloak client's redirect settings if it changes the issuer).
/// </summary>
public static class MobileSettings
{
    private static string Host => DeviceInfo.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost";

    /// <summary>The app's name in the UI (white label). The home-screen name is ApplicationTitle in MobileApp.csproj.</summary>
    public const string AppName = "TV Tracker";

    /// <summary>Mobile BFF ("http" launch profile, port 5110).</summary>
    public static Uri BffAddress => new($"http://{Host}:5110/");

    /// <summary>Keycloak realm (fixed host port 8080 in the AppHost).</summary>
    public static string Authority => $"http://{Host}:8080/realms/tvtracker";

    public const string ClientId = "tvtracker-mobile";
    public const string RedirectUri = "tvtracker://callback";
}

using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker;

/// <summary>
/// The app's name, for white-labelling. It starts as <c>Branding:AppName</c> (the AppHost passes its own to the web app
/// and the services that put the name in text) and follows what an admin sets in Settings, which Identity.API keeps
/// (<see cref="BrandingRefresher"/>). The UI has its own <c>TvTracker.UI.Services.Branding</c>, since the phone doesn't
/// reference ServiceDefaults.
/// </summary>
public sealed class Branding
{
    public const string Section = "Branding";
    public const string DefaultAppName = "TV Tracker";

    private readonly string _configured;
    private volatile string? _fromSettings;

    public Branding(string? appName = null) =>
        _configured = string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName.Trim();

    /// <summary>
    /// The name set in Settings, else the configured one.
    /// </summary>
    public string AppName => _fromSettings ?? _configured;

    /// <summary>
    /// Takes the name from Settings; null or empty goes back to the configured one.
    /// </summary>
    public void Apply(string? appNameFromSettings) =>
        _fromSettings = string.IsNullOrWhiteSpace(appNameFromSettings) ? null : appNameFromSettings.Trim();
}

/// <summary>
/// Keeps <see cref="Branding"/> in step with Settings: asks Identity.API for the app's name when the service starts and
/// every minute after, so a new name reaches emails, notifications and the text services write without a restart.
/// <list type="bullet">
/// <item><b>Only where Identity.API is referenced</b> (the AppHost's <c>WithReference(identityApi)</c>); elsewhere it
/// isn't registered and the configured name stands.</item>
/// <item><b>Never in the way:</b> Identity.API down or slow just leaves the name as it was.</item>
/// </list>
/// </summary>
public sealed class BrandingRefresher(IHttpClientFactory clients, Branding branding, ILogger<BrandingRefresher> logger) : BackgroundService
{
    public const string ClientName = "branding";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var settings = await clients.CreateClient(ClientName).GetFromJsonAsync<SiteBranding>("api/v1/branding", stoppingToken);
                var before = branding.AppName;
                branding.Apply(settings?.AppName);
                if (branding.AppName != before)
                {
                    logger.LogInformation("The app's name is now {AppName}, from Settings", branding.AppName);
                }
            }
            // Whatever goes wrong (Identity.API not up yet, a timeout from the resilience handler, a bad answer): an
            // exception leaving a BackgroundService stops the whole service, and a name isn't worth that.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Couldn't read the app's name from Identity.API; keeping {AppName}", branding.AppName);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private sealed record SiteBranding(string? AppName);
}

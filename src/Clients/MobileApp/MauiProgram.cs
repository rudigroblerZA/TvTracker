using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using TvTracker.Mobile.Services;
using TvTracker.UI.Services;

namespace TvTracker.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android => android
                .OnCreate((activity, _) => HandleDeepLink(activity.Intent?.Data?.ToString()))
                .OnNewIntent((_, intent) => HandleDeepLink(intent?.Data?.ToString())));
#elif IOS || MACCATALYST
            events.AddiOS(ios => ios.OpenUrl((_, url, _) =>
            {
                HandleDeepLink(url?.AbsoluteString);
                return true;
            }));
#endif
        });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Every API call goes through the Mobile BFF, e.g. {bff}/catalog/api/v1/shows.
        var bff = MobileSettings.BffAddress;
        builder.Services.AddTvTrackerUi(name => new Uri(bff, $"{name}/"));

        // Sign-in and tokens
        builder.Services.AddAuthorizationCore();
        builder.Services.AddSingleton<MobileAuthService>();
        builder.Services.AddSingleton<MobileAuthStateProvider>();
        builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<MobileAuthStateProvider>());
        builder.Services.AddSingleton<IAccessTokenProvider, MobileAccessTokenProvider>();
        builder.Services.AddSingleton<IAuthActions, MobileAuthActions>();

        // Offline cache and queued changes
        builder.Services.AddSingleton<SqliteOfflineStore>();
        builder.Services.AddSingleton<IOfflineStore>(sp => sp.GetRequiredService<SqliteOfflineStore>());
        builder.Services.AddSingleton<OfflineSyncService>();

        // Platform features
        builder.Services.AddSingleton<IPushTokenProvider, NotConfiguredPushTokenProvider>();
        builder.Services.AddScoped<IPlatformServices, MobilePlatformServices>();
        builder.Services.AddSingleton<DeepLinkService>();

        var app = builder.Build();

        _deepLinks = app.Services.GetRequiredService<DeepLinkService>();
        app.Services.GetRequiredService<OfflineSyncService>().Start();

        // Clear cached personal data when the user signs out.
        var auth = app.Services.GetRequiredService<MobileAuthService>();
        var store = app.Services.GetRequiredService<SqliteOfflineStore>();
        auth.SignedInChanged += async () =>
        {
            if ((await auth.GetUserAsync()).Identity?.IsAuthenticated != true)
            {
                await store.ClearAsync();
            }
        };

        return app;
    }

    private static DeepLinkService? _deepLinks;

    private static void HandleDeepLink(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _deepLinks?.Handle(uri);
        }
    }
}

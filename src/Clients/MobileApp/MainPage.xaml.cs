using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TvTracker.Mobile.Services;

namespace TvTracker.Mobile;

public partial class MainPage : ContentPage
{
    private readonly DeepLinkService _deepLinks;

    public MainPage()
    {
        InitializeComponent();

        _deepLinks = IPlatformApplication.Current!.Services.GetRequiredService<DeepLinkService>();
        _deepLinks.LinkReceived += Navigate;

        blazorWebView.BlazorWebViewInitialized += (_, _) =>
        {
            if (_deepLinks.TakePending() is { } path)
            {
                Navigate(path);
            }
        };
    }

    /// <summary>Navigates the Blazor UI, e.g. to "shows/{id}" from a tvtracker://shows/{id} link.</summary>
    private void Navigate(string path) =>
        MainThread.BeginInvokeOnMainThread(async () =>
            await blazorWebView.TryDispatchAsync(sp => sp.GetRequiredService<NavigationManager>().NavigateTo(path)));
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TvTracker.UI.Services;

public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared UI's API clients and session state. <paramref name="baseAddressFor"/> returns the
    /// base address for each named client (see <see cref="ApiClients"/>). The host must also register
    /// <see cref="IAccessTokenProvider"/>, <see cref="IAuthActions"/>, <see cref="IPlatformServices"/>
    /// an AuthenticationStateProvider and OpenFeature's IFeatureClient; <see cref="IOfflineStore"/> defaults to online-only.
    /// <paramref name="appName"/>, <paramref name="operatorName"/> and <paramref name="contactEmail"/> white-label the UI
    /// (<see cref="Branding"/>); <paramref name="catalogSource"/> is the catalog's provider, for the footer's credit.
    /// </summary>
    public static IServiceCollection AddTvTrackerUi(this IServiceCollection services, Func<string, Uri> baseAddressFor, string? appName = null,
        string? operatorName = null, string? contactEmail = null, string? catalogSource = null, string? operatorAddress = null,
        string? companyNumber = null, string? governingLaw = null)
    {
        foreach (var name in ApiClients.All)
        {
            var baseAddress = baseAddressFor(name);
            services.AddHttpClient(name, client => client.BaseAddress = baseAddress);
        }

        services.TryAddScoped<IOfflineStore, OnlineOnly>();
        services.AddScoped<ConnectionState>();
        services.TryAddScoped<ClientInfo>();
        services.AddScoped<UserSession>();
        services.AddScoped<BrandingLoader>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ShowTagStore>();
        services.AddScoped<FeatureFlagStore>();
        services.AddScoped<SpoilerShield>();
        services.AddScoped<ThemeStore>();
        services.AddSingleton(new Branding(appName, operatorName, contactEmail, catalogSource, operatorAddress, companyNumber, governingLaw));

        services.AddScoped<IdentityClient>();
        services.AddScoped<CatalogClient>();
        services.AddScoped<TrackingClient>();
        services.AddScoped<RatingsClient>();
        services.AddScoped<RecommendationsClient>();
        services.AddScoped<NotificationsClient>();
        services.AddScoped<AnalyticsClient>();
        services.AddScoped<SocialClient>();
        services.AddScoped<ResearchClient>();

        return services;
    }
}

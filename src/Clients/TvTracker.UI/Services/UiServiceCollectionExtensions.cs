using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TvTracker.UI.Services;

public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared UI's API clients and session state. <paramref name="baseAddressFor"/> returns the
    /// base address for each named client (see <see cref="ApiClients"/>). The host must also register
    /// <see cref="IAccessTokenProvider"/>, <see cref="IAuthActions"/>, <see cref="IPlatformServices"/>
    /// and an AuthenticationStateProvider; <see cref="IOfflineStore"/> defaults to online-only.
    /// </summary>
    public static IServiceCollection AddTvTrackerUi(this IServiceCollection services, Func<string, Uri> baseAddressFor)
    {
        foreach (var name in ApiClients.All)
        {
            var baseAddress = baseAddressFor(name);
            services.AddHttpClient(name, client => client.BaseAddress = baseAddress);
        }

        services.TryAddScoped<IOfflineStore, OnlineOnly>();
        services.AddScoped<ConnectionState>();
        services.AddScoped<UserSession>();

        services.AddScoped<IdentityClient>();
        services.AddScoped<CatalogClient>();
        services.AddScoped<TrackingClient>();
        services.AddScoped<RatingsClient>();
        services.AddScoped<RecommendationsClient>();
        services.AddScoped<NotificationsClient>();
        services.AddScoped<AnalyticsClient>();

        return services;
    }
}

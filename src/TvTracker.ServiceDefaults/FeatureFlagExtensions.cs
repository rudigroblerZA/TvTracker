using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenFeature;
using OpenFeature.DependencyInjection.Providers.Flagd;
using OpenFeature.Hosting.Providers.Memory;
using TvTracker;

namespace Microsoft.Extensions.Hosting;

/// <summary>OpenFeature feature flags, served by flagd (see <see cref="FeatureFlags"/>).</summary>
public static class FeatureFlagExtensions
{
    /// <summary>
    /// Registers OpenFeature's <see cref="IFeatureClient"/>, backed by flagd when <c>ConnectionStrings:flagd</c> is set
    /// (the AppHost sets it). Each evaluation asks flagd, so an edit to flagd.json applies at once, without a restart.
    /// The provider's own cache stays off: in OpenFeature.Providers.Flagd 0.8.0 every cached evaluation failed with a
    /// NullReferenceException (and so returned the default). Without flagd, an empty in-memory provider answers, so
    /// every flag takes its default.
    /// </summary>
    public static TBuilder AddFeatureFlags<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var flagd = builder.Configuration.GetConnectionString("flagd");
        builder.Services.AddOpenFeature(openFeature =>
        {
            if (Uri.TryCreate(flagd, UriKind.Absolute, out var uri))
            {
                openFeature.AddFlagdProvider(options =>
                {
                    options.Host = uri.Host;
                    options.Port = uri.Port;
                    options.UseTls = uri.Scheme == Uri.UriSchemeHttps;
                });
            }
            else
            {
                openFeature.AddInMemoryProvider(_ => { });
            }
        });

        // Plans: where Identity is reachable (the AppHost references it), the caller's plan joins the flag context. A
        // service with its own copy of plans registers that instead (AddUserPlans), which replaces this.
        if (builder.Configuration.GetSection("services:identity-api").Exists())
        {
            builder.Services.AddMemoryCache();
            builder.Services.AddHttpClient(IdentityUserPlans.ClientName, client => client.BaseAddress = new Uri("https+http://identity-api/"));
            builder.Services.TryAddScoped<IUserPlans, IdentityUserPlans>();
        }
        return builder;
    }

    /// <summary>
    /// Answers 404 while <paramref name="flag"/> is off for the caller, as if the endpoint didn't exist. Evaluated per
    /// request with the caller's context (<see cref="FeatureFlags.ContextFor"/>), so flags can target users, and with
    /// their plan (<see cref="IUserPlans"/>, when registered), so a flag can be part of some plans only.
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder endpoints, string flag) where TBuilder : IEndpointConventionBuilder =>
        endpoints.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var client = http.RequestServices.GetRequiredService<IFeatureClient>();
            var plan = http.RequestServices.GetService<IUserPlans>() is { } plans ? await plans.PlanAsync(http) : null;
            var enabled = await client.GetBooleanValueAsync(flag, FeatureFlags.DefaultValue, FeatureFlags.ContextFor(http.User, plan),
                cancellationToken: http.RequestAborted);
            return enabled ? await next(context) : TypedResults.NotFound();
        });

    /// <summary>Uses <typeparamref name="T"/> (a lookup in the service's own copy of plans) as the caller's plan for flags.</summary>
    public static IServiceCollection AddUserPlans<T>(this IServiceCollection services) where T : class, IUserPlans
    {
        services.AddMemoryCache();
        services.Replace(ServiceDescriptor.Scoped<IUserPlans, T>());
        return services;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        return builder;
    }

    /// <summary>
    /// Answers 404 while <paramref name="flag"/> is off for the caller, as if the endpoint didn't exist. Evaluated per
    /// request with the caller's context (<see cref="FeatureFlags.ContextFor"/>), so flags can target users.
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder endpoints, string flag) where TBuilder : IEndpointConventionBuilder =>
        endpoints.AddEndpointFilter(async (context, next) =>
        {
            var client = context.HttpContext.RequestServices.GetRequiredService<IFeatureClient>();
            var enabled = await client.GetBooleanValueAsync(flag, FeatureFlags.DefaultValue, FeatureFlags.ContextFor(context.HttpContext.User),
                cancellationToken: context.HttpContext.RequestAborted);
            return enabled ? await next(context) : TypedResults.NotFound();
        });
}

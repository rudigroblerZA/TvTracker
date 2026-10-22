---
name: codeatout-dotnet-configuration
description: Designs and implements typed, validated, and securely deployed configuration for .NET applications. Use when adding or changing application settings, configuration binding, or environment overrides.
---

# .NET Application Configuration

Use this skill for .NET configuration that is consumed by application services,
authentication, API documentation, integrations, or infrastructure clients.
Apply `codeatout-acl-entra-authentication` for Entra authentication settings;
it defines the additional security requirements for that domain.

## Discover first

Inspect the host's configuration providers and precedence, appsettings files,
environment-specific overrides, deployment manifests, secret store integration,
existing option types, configuration extensions, and tests. Reuse the
repository's configuration names, validation approach, and dependency
lifetimes. Do not add a competing configuration provider, reload strategy, or
options pattern as an incidental change.

Classify every value before adding it:

| Classification | Examples | Location |
| --- | --- | --- |
| Non-secret deployment setting | Service URL, database name, tenant ID, feature limit | Base settings shape, environment variables, or deployment configuration |
| Secret | Password, client secret, connection key, certificate | Approved secret store or deployment secret injection only |
| Derived value | Authority URL, OAuth scope, endpoint path | Typed configuration property; do not duplicate it in settings |

## Libraries and patterns

Use the .NET configuration stack already present in the service:

| Library | Purpose |
| --- | --- |
| `Microsoft.Extensions.Configuration` | Reads the composed configuration tree from application, environment, and deployment providers. |
| `Microsoft.Extensions.Configuration.Binder` | Binds a named section into a typed configuration record. |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | Registers validated, immutable configuration for consumers. |
| `Microsoft.Extensions.Options` | Applies framework or library options through `IConfigureOptions<T>` when that library uses the options pipeline. |

For a stable, startup-only setting, bind once, validate, and register the
immutable record as a singleton. Use `IOptions<T>`, `IOptionsSnapshot<T>`, or
`IOptionsMonitor<T>` only when the consuming framework or a deliberate reload
requirement calls for it. Do not use raw `IConfiguration` throughout business
code.

## Typed configuration contract

Give each cohesive settings group a dedicated record with a `SectionName`
constant. Model only settings the application owns, require values that must be
present, and compute values that can be derived safely.

```csharp
public sealed record IdentityConfiguration
{
    public const string SectionName = "Identity";

    public required string Instance { get; init; }

    public required string TenantId { get; init; }

    public required string ClientId { get; init; }

    public required string Audience { get; init; }

    public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}";

    public string UserImpersonationScope => $"{Audience}/user_impersonation";
}
```

`required` protects object construction but does not prove configuration values
are present or valid at runtime. Validate required strings, URI shapes, ranges,
and cross-property rules when binding:

```csharp
public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IdentityConfiguration identityConfiguration = configuration
            .GetRequiredSection(IdentityConfiguration.SectionName)
            .Get<IdentityConfiguration>()
            ?? throw new InvalidOperationException(
                $"Configuration section '{IdentityConfiguration.SectionName}' is invalid.");

        if (string.IsNullOrWhiteSpace(identityConfiguration.Instance) ||
            string.IsNullOrWhiteSpace(identityConfiguration.TenantId) ||
            string.IsNullOrWhiteSpace(identityConfiguration.ClientId) ||
            string.IsNullOrWhiteSpace(identityConfiguration.Audience))
        {
            throw new InvalidOperationException(
                $"Configuration section '{IdentityConfiguration.SectionName}' is incomplete.");
        }

        services.AddSingleton(identityConfiguration);

        return services;
    }
}
```

Place the binding and framework registration in a focused
`IServiceCollection` extension at the composition boundary. This keeps
`Program.cs` declarative and allows configuration consumers to depend on the
typed record rather than a stringly typed configuration tree.

## Configuration consumers

Inject the typed configuration into the framework extension or options
configurator that needs it. Keep derived values in the record so consumers do
not reassemble URLs or scopes differently.

```csharp
public sealed class ConfigureOAuthOptions(IdentityConfiguration identityConfiguration)
    : IConfigureOptions<OAuthOptions>
{
    public void Configure(OAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AuthorizationEndpoint =
            $"{identityConfiguration.Authority}/oauth2/v2.0/authorize";
        options.TokenEndpoint =
            $"{identityConfiguration.Authority}/oauth2/v2.0/token";
        options.Scope = identityConfiguration.UserImpersonationScope;
    }
}
```

Do not expose secrets to browser configuration, API documentation, logs,
health checks, exception messages, or telemetry. A public client identifier is
not a secret, but it must still be supplied through deployment configuration
instead of hard-coded.

## Settings and deployment contract

Keep the base settings file structural and non-secret. It documents the
configuration shape without providing environment credentials:

```json
{
  "Identity": {
    "Instance": "https://login.microsoftonline.com",
    "TenantId": null,
    "ClientId": null,
    "Audience": null
  },
  "PartnerApi": {
    "BaseUrl": "",
    "Audience": ""
  }
}
```

Use the standard double-underscore convention when deployment systems supply
nested settings as environment variables:

```text
Identity__TenantId
Identity__ClientId
Identity__Audience
PartnerApi__BaseUrl
```

Store a setting such as `Identity__ClientSecret` only in the platform's
approved secret store or secret-injection mechanism. Do not place its value in
appsettings files, Helm values files, ConfigMaps, source-controlled pipeline
variables, or test fixtures. Keep deployment configuration and secret manifests
aligned with the typed record's section and property names.

When a repository already uses a secret provider such as Azure Key Vault,
configure it at host startup using the workload identity or approved credential
chain. Make provider precedence, initial-load failure, and reload behaviour
explicit: a secret reload must not leave a consumer with a partial
configuration or silently mask an unavailable required secret.

## Validate

Test successful binding, missing sections, empty required values, malformed
values, and derived properties. Test the deployment variable names against the
configuration contract, and ensure production startup fails clearly when
required configuration is absent rather than deferring failure to the first
request. Review logs and diagnostic endpoints to confirm they do not disclose
secrets or confidential configuration.

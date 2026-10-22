---
name: codeatout-acl-entra-authentication
description: Adds Microsoft Entra ID authentication and application authorization to a service. Use when securing an API with Entra ID, OAuth 2.0, OpenID Connect, or client application access control.
---

# Entra Authentication and Application Authorization

## Before changing code

1. Inspect the service's hosting model, current authentication scheme,
   authorization policies, API documentation, and deployment configuration.
   Apply `codeatout-dotnet-configuration` for typed binding, startup
   validation, deployment overrides, and secret separation.
2. Confirm the intended identity provider tenant, audience, API scopes or app
   roles, calling clients, and whether the API is public or internal.
3. Reuse the repository's established configuration, secrets, and test
   patterns. Do not assume a particular project layout or authentication
   library.

## Implementation principles

- Use supported Microsoft identity middleware to validate issuer, audience,
  signature, lifetime, and applicable claims on every protected request.
- Prefer app roles or narrowly scoped OAuth permissions. Authorize access to
  the requested resource, not only to the route.
- Keep tenant IDs, client IDs, and endpoint settings in configuration. Keep
  secrets in the platform's approved secret store; never commit them.
- Configure interactive API documentation only when the deployed environment
  supports it, and use OAuth authorization-code flow with PKCE where applicable.
- Provide a test authentication path only in test environments and ensure it
  cannot be enabled in production.

## Validate

- Test missing, malformed, expired, wrong-audience, and insufficient-privilege
  tokens.
- Test the permitted client/application claims and denied callers.
- Confirm configuration is supplied through deployment settings and no
  credentials or tenant-specific values are logged.

## Reference pattern

For an ASP.NET Core API that needs a default-deny application allow-list, bind
the approved client identifiers from configuration and require a presenting
client claim. Confirm the claim type issued by the configured Entra flow before
using this pattern:

```csharp
var allowedClientIds = configuration
    .GetSection("Authorization:AllowedClientIds")
    .Get<string[]>()?
    .ToHashSet(StringComparer.Ordinal)
    ?? throw new InvalidOperationException("An application allow-list is required");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => configuration.Bind("Entra", options));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
        {
            var clientId = context.User.FindFirst("azp")?.Value
                ?? context.User.FindFirst("appid")?.Value;

            return clientId is not null && allowedClientIds.Contains(clientId);
        })
        .Build();
});
```

Use explicit anonymous endpoints only for intentionally public operations such
as a health check. If Swagger UI is exposed, configure its OAuth client and PKCE
only outside production and never put a client secret in browser configuration.

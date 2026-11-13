using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace WebApp.Services;

/// <summary>
/// Sign-in through the web app's own address (<c>WebApp:PublicUrl</c>), for a web app reached through one forwarded
/// address only, such as a cloud session's preview: the browser never sees Keycloak's.
/// <list type="bullet">
/// <item><b>Keycloak behind the web app:</b> <c>/realms/tvtracker/…</c> and <c>/resources/…</c> (the login pages and their
/// assets) are proxied to Keycloak with <c>X-Forwarded-*</c> naming the public address (Keycloak reads them,
/// <c>KC_PROXY_HEADERS</c>), so every link and form on its pages points back through the web app.</item>
/// <item><b>The browser's redirects:</b> the authorize and logout redirects, and the addresses Keycloak sends the browser
/// back to, are rewritten to the public address. The token exchange and discovery still go to Keycloak directly, so the
/// tokens' issuer is Keycloak's own address and the services validate them as before.</item>
/// <item><b>No pushed authorization:</b> after a pushed request the handler builds the authorize address itself, which
/// would skip the rewrite.</item>
/// <item><b>Framed previews:</b> Keycloak's pages forbid framing (<c>X-Frame-Options</c>, CSP <c>frame-ancestors</c>);
/// those headers are dropped on the way through, since a preview pane may show the page in a frame.</item>
/// <item><b>Redirect addresses:</b> Identity.API adds <c>{PublicUrl}/*</c> to the web client's allowed redirects
/// (<c>Keycloak:WebPublicUrl</c>, RealmSecretsSeeder), which the realm file has only for localhost.</item>
/// </list>
/// Off unless <c>WebApp:PublicUrl</c> is set, so a local run signs in at Keycloak's address as before.
/// </summary>
public static class SignInThroughWebApp
{
    private const string Cluster = "keycloak";

    /// <summary>
    /// The public address when sign-in goes through the web app, else null.
    /// </summary>
    public static Uri? PublicUrl(IConfiguration configuration) =>
        Uri.TryCreate(configuration["WebApp:PublicUrl"], UriKind.Absolute, out var url) ? url : null;

    public static void AddProxy(IServiceCollection services, string keycloakUrl, Uri publicUrl, bool allowFraming)
    {
        RouteConfig Route(string id, string path) => new() { RouteId = id, ClusterId = Cluster, Match = new() { Path = path } };

        services.AddReverseProxy()
            .LoadFromMemory(
                [Route("keycloak-realm", $"/realms/{IdentitySettings.Realm}/{{**rest}}"), Route("keycloak-resources", "/resources/{**rest}")],
                [new ClusterConfig { ClusterId = Cluster, Destinations = new Dictionary<string, DestinationConfig> { ["keycloak"] = new() { Address = keycloakUrl } } }])
            .AddTransforms(context =>
            {
                context.AddXForwarded(ForwardedTransformActions.Off);
                context.AddRequestHeader("X-Forwarded-Host", publicUrl.Authority, append: false);
                context.AddRequestHeader("X-Forwarded-Proto", publicUrl.Scheme, append: false);
                context.AddRequestHeader("X-Forwarded-Port", publicUrl.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), append: false);
                // Keycloak forbids framing its login pages. That stays so when deployed (a framed login form can be
                // clickjacked); in development it's lifted, for a preview pane that shows the app in a frame.
                if (allowFraming)
                {
                    context.AddResponseHeaderRemove("X-Frame-Options");
                    context.AddResponseHeaderRemove("Content-Security-Policy");
                }
            });
    }

    /// <summary>
    /// Sends the browser to Keycloak through the public address, and back to the web app at it.
    /// </summary>
    public static void Apply(OpenIdConnectOptions options, string authority, Uri publicUrl)
    {
        var origin = publicUrl.GetLeftPart(UriPartial.Authority);
        var publicAuthority = $"{origin}/realms/{IdentitySettings.Realm}";
        string Public(string? address) => address is null ? publicAuthority : address.Replace(authority, publicAuthority, StringComparison.OrdinalIgnoreCase);

        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

        var signIn = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            await signIn(context);
            context.ProtocolMessage.IssuerAddress = Public(context.ProtocolMessage.IssuerAddress);
            context.ProtocolMessage.RedirectUri = origin + options.CallbackPath;
            // The token exchange sends the redirect address the challenge recorded, which must be this one.
            context.Properties.Items[OpenIdConnectDefaults.RedirectUriForCodePropertiesKey] = context.ProtocolMessage.RedirectUri;
        };

        var signOut = options.Events.OnRedirectToIdentityProviderForSignOut;
        options.Events.OnRedirectToIdentityProviderForSignOut = async context =>
        {
            await signOut(context);
            context.ProtocolMessage.IssuerAddress = Public(context.ProtocolMessage.IssuerAddress);
            context.ProtocolMessage.PostLogoutRedirectUri = origin + options.SignedOutCallbackPath;
        };
    }
}

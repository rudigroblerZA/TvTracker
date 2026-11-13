using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using StackExchange.Redis;
﻿using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Http.Resilience;
using TvTracker.UI.Models;
using TvTracker.UI.Services;
using WebApp.Components;
using WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddFeatureFlags();

// Server-side token store (Redis), so access tokens never go to the browser.
builder.AddRedisDistributedCache("redis");
builder.Services.AddSingleton<ServerTokenStore>();
builder.Services.AddHttpClient("keycloak");

// The keys that protect the auth cookie and antiforgery tokens, kept in Redis under a fixed application name: in
// memory (the default in a container) every restart or deploy would sign everyone out, and two copies of the web app
// couldn't read each other's cookies.
builder.Services.AddDataProtection().SetApplicationName("tvtracker-web");
builder.Services.AddOptions<KeyManagementOptions>().Configure<IConnectionMultiplexer>((options, redis) =>
    options.XmlRepository = new RedisXmlRepository(() => redis.GetDatabase(), "tvtracker-web:data-protection-keys"));

// Sign-in with Keycloak: an auth cookie for the browser, tokens kept server-side.
var authority = IdentitySettings.GetAuthority(builder.Configuration);
var publicUrl = SignInThroughWebApp.PublicUrl(builder.Configuration);
if (publicUrl is not null)
{
    SignInThroughWebApp.AddProxy(builder.Services, builder.Configuration["Identity:Url"]!, publicUrl, allowFraming: builder.Environment.IsDevelopment());
}
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "tvtracker";
        // Deployed, the site is HTTPS only (HSTS and the redirect below), and so is its cookie.
        if (!builder.Environment.IsDevelopment())
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        }
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = builder.Configuration["Identity:WebClientId"] ?? "tvtracker-web";
        options.ClientSecret = builder.Configuration["Identity:WebClientSecret"];
        options.ResponseType = "code";
        options.UsePkce = true;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SaveTokens = false;
        options.Scope.Add("email"); // "openid" and "profile" are requested by default
        options.TokenValidationParameters.NameClaimType = "name";

        options.Events.OnTokenValidated = async context =>
        {
            var userId = context.Principal?.FindFirst("sub")?.Value;
            var response = context.TokenEndpointResponse;
            if (userId is null || response?.AccessToken is null)
            {
                return;
            }

            var store = context.HttpContext.RequestServices.GetRequiredService<ServerTokenStore>();
            var expiresIn = int.TryParse(response.ExpiresIn, out var seconds) ? seconds : 300;
            await store.SaveAsync(userId, new StoredTokens(
                response.AccessToken, response.RefreshToken, response.IdToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn)));
        };

        options.Events.OnRedirectToIdentityProviderForSignOut = async context =>
        {
            // Keycloak skips its logout confirmation page when it gets the id token.
            var userId = context.HttpContext.User.FindFirst("sub")?.Value;
            if (userId is not null)
            {
                var store = context.HttpContext.RequestServices.GetRequiredService<ServerTokenStore>();
                context.ProtocolMessage.IdTokenHint = (await store.GetAsync(userId))?.IdToken;
                await store.RemoveAsync(userId);
            }
        };

        if (publicUrl is not null)
        {
            SignInThroughWebApp.Apply(options, authority, publicUrl);
        }
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The web app calls each service directly; "https+http://<name>" is resolved by Aspire service discovery.
builder.Services.AddTvTrackerUi(name => new Uri($"https+http://{name}-api/"), builder.Configuration["Branding:AppName"],
    builder.Configuration["Branding:Operator"], builder.Configuration["Branding:ContactEmail"], builder.Configuration["Catalog:Provider"]);

// Services rate limit per visitor (the UI forwards each browser's IP), so a 429 is one visitor's answer, not a sign the
// service is down. Don't retry it (every retry spends another of their requests) and don't let it trip the circuit
// breaker, which this process shares across all visitors and which, once open, fails everyone's calls.
builder.Services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
{
    options.Retry.ShouldHandle = args => ValueTask.FromResult(IsTransientExceptTooManyRequests(args.Outcome));
    options.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(IsTransientExceptTooManyRequests(args.Outcome));
});

static bool IsTransientExceptTooManyRequests(Polly.Outcome<HttpResponseMessage> outcome) =>
    outcome.Result?.StatusCode != System.Net.HttpStatusCode.TooManyRequests && HttpClientResiliencePredicates.IsTransient(outcome);
builder.Services.AddScoped<IAccessTokenProvider, WebAccessTokenProvider>();
builder.Services.AddScoped<IAuthActions, WebAuthActions>();
builder.Services.AddScoped<IPlatformServices, WebPlatformServices>();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// In development, say once per address where the web app is reached from outside this machine (a forwarded preview,
// a tunnel): that's the WebApp__PublicUrl sign-in needs there (SignInThroughWebApp).
if (app.Environment.IsDevelopment() && publicUrl is null)
{
    var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
    var origins = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PublicAddress");
    app.Use((http, next) =>
    {
        var forwardedHost = http.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
        var host = forwardedHost ?? http.Request.Host.Host;
        if (host is { Length: > 0 } && host is not ("localhost" or "127.0.0.1" or "[::1]") && seen.TryAdd(host, true))
        {
            origins.LogWarning("Reached from outside at Host {Host}, X-Forwarded-Host {ForwardedHost}, X-Forwarded-Proto {ForwardedProto}; " +
                "set WebApp__PublicUrl to that address for sign-in to work there",
                http.Request.Host.Value, forwardedHost, http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault());
        }
        return next(http);
    });
}

// Headers every response carries: no MIME sniffing, and no full addresses in referrers to other sites. Framing by
// other sites (clickjacking) is already refused: Blazor sends "frame-ancestors 'self'" and antiforgery X-Frame-Options.
app.Use((http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    return next(http);
});

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

if (publicUrl is not null)
{
    app.MapReverseProxy();
}

app.MapGet("/account/login", (string? returnUrl) =>
    TypedResults.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }));

app.MapGet("/account/logout", () =>
    TypedResults.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

// Where-to-watch clicks (affiliate links): Catalog.API records the click and says where to send the viewer, a link it
// already lists for the show, wrapped by an affiliate program when one covers the service. A plain GET, so the show
// page's links are ordinary links (open in a new tab, middle-click) and the phone can open them in the browser.
app.MapGet("/go/{showId:guid}", async (Guid showId, string provider, int type, string region, string surface, HttpContext http,
    IHttpClientFactory clients, ServerTokenStore tokens, ILoggerFactory loggers) =>
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/outbound")
    {
        Content = JsonContent.Create(new { showId, provider, type, region, surface })
    };
    // Signed in, the click carries the user's plan (nothing else of theirs); the browser's address is only for the rate limit.
    if (http.User.FindFirst("sub")?.Value is { } userId && await tokens.GetValidAccessTokenAsync(userId, http.RequestAborted) is { } token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    if (http.Connection.RemoteIpAddress is { } address)
    {
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", address.ToString());
    }

    try
    {
        using var response = await clients.CreateClient(ApiClients.Catalog).SendAsync(request, http.RequestAborted);
        if (response.IsSuccessStatusCode
            && await response.Content.ReadFromJsonAsync<OutboundLink>(http.RequestAborted) is { } link
            && Uri.TryCreate(link.Url, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
        {
            return Results.Redirect(target.AbsoluteUri);
        }
        loggers.CreateLogger("Outbound").LogWarning("Where-to-watch click for {ShowId} on {Provider} answered {Status}", showId, provider, response.StatusCode);
    }
    catch (HttpRequestException ex)
    {
        loggers.CreateLogger("Outbound").LogWarning(ex, "Where-to-watch click for {ShowId} on {Provider} failed", showId, provider);
    }
    // Back to the show, whose links are direct while the flag is off or the catalog can't be reached.
    return Results.Redirect($"/shows/{showId}");
});

TestAffiliateNetwork.Map(app);
TestPaymentProvider.Map(app);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(TvTracker.UI.Routes).Assembly);

app.Run();

// Only allow local return URLs, so the login endpoint can't be used as an open redirect.
static string SafeReturnUrl(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
        ? returnUrl
        : "/";

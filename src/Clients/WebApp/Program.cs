using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Http.Resilience;
using TvTracker.UI.Services;
using WebApp.Components;
using WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Server-side token store (Redis), so access tokens never go to the browser.
builder.AddRedisDistributedCache("redis");
builder.Services.AddSingleton<ServerTokenStore>();
builder.Services.AddHttpClient("keycloak");

// Sign-in with Keycloak: an auth cookie for the browser, tokens kept server-side.
var authority = IdentitySettings.GetAuthority(builder.Configuration);
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "tvtracker";
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
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The web app calls each service directly; "https+http://<name>" is resolved by Aspire service discovery.
builder.Services.AddTvTrackerUi(name => new Uri($"https+http://{name}-api/"));

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

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/account/login", (string? returnUrl) =>
    TypedResults.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }));

app.MapGet("/account/logout", () =>
    TypedResults.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(TvTracker.UI.Routes).Assembly);

app.Run();

// Only allow local return URLs, so the login endpoint can't be used as an open redirect.
static string SafeReturnUrl(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
        ? returnUrl
        : "/";

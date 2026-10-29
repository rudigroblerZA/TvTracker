using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IdentityModel.Client;
using IdentityModel.OidcClient;
using IdentityModel.OidcClient.Browser;
using Microsoft.AspNetCore.Components.Authorization;
using TvTracker.UI.Services;
// MAUI's implicit usings bring in Microsoft.Maui.ApplicationModel.IBrowser (the system browser, with OpenAsync),
// which takes the plain name; the OIDC client needs its own browser interface.
using OidcBrowser = IdentityModel.OidcClient.Browser.IBrowser;

namespace TvTracker.Mobile.Services;

/// <summary>Opens the sign-in page in the system browser and returns the redirect (tvtracker://callback?...).</summary>
public sealed class MauiAuthBrowser : OidcBrowser
{
    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = new Uri(options.StartUrl),
                CallbackUrl = new Uri(options.EndUrl)
            });

            var query = string.Join("&", result.Properties.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            return new BrowserResult { ResultType = BrowserResultType.Success, Response = $"{options.EndUrl}?{query}" };
        }
        catch (TaskCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.UserCancel, Error = "Sign-in was cancelled." };
        }
    }
}

public record MobileTokens(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Keycloak sign-in for the mobile app: authorization code + PKCE in the system browser,
/// tokens in secure storage, refresh on demand.
/// </summary>
public sealed class MobileAuthService
{
    private const string StorageKey = "tvtracker.tokens";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly OidcClient _client = new(new OidcClientOptions
    {
        Authority = MobileSettings.Authority,
        ClientId = MobileSettings.ClientId,
        Scope = "openid profile email offline_access",
        RedirectUri = MobileSettings.RedirectUri,
        PostLogoutRedirectUri = MobileSettings.RedirectUri,
        Browser = new MauiAuthBrowser(),
        // Local Keycloak runs on plain http.
        Policy = new Policy { Discovery = new DiscoveryPolicy { RequireHttps = false } }
    });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MobileTokens? _tokens;
    private bool _loaded;

    public event Action? SignedInChanged;

    public async Task<bool> SignInAsync()
    {
        var result = await _client.LoginAsync(new LoginRequest());
        if (result.IsError)
        {
            return false;
        }

        await SaveAsync(new MobileTokens(result.AccessToken, result.RefreshToken, result.IdentityToken, result.AccessTokenExpiration));
        SignedInChanged?.Invoke();
        return true;
    }

    public async Task SignOutAsync()
    {
        var idToken = (await LoadAsync())?.IdToken;
        await SaveAsync(null);
        SignedInChanged?.Invoke();

        try
        {
            await _client.LogoutAsync(new LogoutRequest { IdTokenHint = idToken });
        }
        catch (Exception)
        {
            // Local sign-out already happened; ending the Keycloak session is best effort.
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var tokens = await LoadAsync();
            if (tokens is null)
            {
                return null;
            }
            if (tokens.ExpiresAtUtc - RefreshMargin > DateTimeOffset.UtcNow)
            {
                return tokens.AccessToken;
            }
            if (tokens.RefreshToken is null)
            {
                return null;
            }

            var refreshed = await _client.RefreshTokenAsync(tokens.RefreshToken);
            if (refreshed.IsError)
            {
                // Offline: keep the old token (calls will fail and use the offline cache).
                // Rejected refresh token: sign out.
                if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
                {
                    await SaveAsync(null);
                    SignedInChanged?.Invoke();
                    return null;
                }
                return tokens.AccessToken;
            }

            var updated = new MobileTokens(refreshed.AccessToken, refreshed.RefreshToken ?? tokens.RefreshToken,
                refreshed.IdentityToken ?? tokens.IdToken, refreshed.AccessTokenExpiration);
            await SaveAsync(updated);
            return updated.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The signed-in user from the stored id token (no network call).</summary>
    public async Task<ClaimsPrincipal> GetUserAsync()
    {
        var tokens = await LoadAsync();
        if (tokens?.IdToken is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = ReadJwtClaims(tokens.IdToken);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "keycloak", "name", "role"));
    }

    private async Task<MobileTokens?> LoadAsync()
    {
        if (_loaded)
        {
            return _tokens;
        }

        var json = await SecureStorage.Default.GetAsync(StorageKey);
        _tokens = json is null ? null : JsonSerializer.Deserialize<MobileTokens>(json);
        _loaded = true;
        return _tokens;
    }

    private async Task SaveAsync(MobileTokens? tokens)
    {
        _tokens = tokens;
        _loaded = true;
        if (tokens is null)
        {
            SecureStorage.Default.Remove(StorageKey);
        }
        else
        {
            await SecureStorage.Default.SetAsync(StorageKey, JsonSerializer.Serialize(tokens));
        }
    }

    private static List<Claim> ReadJwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var claims = new List<Claim>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                claims.AddRange(property.Value.EnumerateArray().Select(v => new Claim(property.Name, v.ToString())));
            }
            else
            {
                claims.Add(new Claim(property.Name, property.Value.ToString()));
            }
        }
        return claims;
    }
}

public sealed class MobileAuthStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly MobileAuthService _auth;

    public MobileAuthStateProvider(MobileAuthService auth)
    {
        _auth = auth;
        _auth.SignedInChanged += OnChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync() => new(await _auth.GetUserAsync());

    private void OnChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose() => _auth.SignedInChanged -= OnChanged;
}

public sealed class MobileAccessTokenProvider(MobileAuthService auth) : IAccessTokenProvider
{
    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => auth.GetAccessTokenAsync();
}

public sealed class MobileAuthActions(MobileAuthService auth) : IAuthActions
{
    public async Task SignInAsync(string? returnUrl = null) => await auth.SignInAsync();

    public Task SignOutAsync() => auth.SignOutAsync();
}

using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace TvTracker.LoadGen;

/// <summary>
/// One user's sign-in, for requests through the BFF: the token is kept and renewed when it nears expiry (the point
/// <see cref="Keycloak.SignInAsync"/> gives), so a long run doesn't carry on with 401s.
/// </summary>
public sealed class Session(Keycloak keycloak, string username, string password, string bffUrl)
{
    private string? token;
    private DateTimeOffset refreshAt;

    /// <summary>
    /// A request to <paramref name="path"/> on the BFF, signed in as this user, with <paramref name="body"/> as JSON.
    /// </summary>
    public async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (token is null || DateTimeOffset.UtcNow >= refreshAt)
        {
            (token, refreshAt) = await keycloak.SignInAsync(username, password, ct);
        }
        var request = new HttpRequestMessage(method, bffUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }
}

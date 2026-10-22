using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TvTracker.UI.Services;

/// <summary>Thrown when the API says the user isn't signed in (or the session expired).</summary>
public sealed class SignInRequiredException() : Exception("Please sign in again.");

/// <summary>
/// Shared HTTP plumbing for the typed API clients: bearer tokens, JSON, 404-as-null,
/// and (on mobile) serving cached responses and queueing changes while offline.
/// </summary>
public abstract class ApiClientBase(
    IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens, IOfflineStore offline, ConnectionState connection, ClientInfo client)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>GET; returns null on 404. With <paramref name="cacheForOffline"/>, the last response is served when offline.</summary>
    protected async Task<T?> GetAsync<T>(string client, string url, CancellationToken ct, bool cacheForOffline = false)
    {
        var cacheKey = $"{client}:{url}";
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, url, null, ct);
            using var response = await httpClientFactory.CreateClient(client).SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            {
                return default;
            }
            await EnsureSuccessAsync(response);

            var json = await response.Content.ReadAsStringAsync(ct);
            connection.ReportOnline();

            if (cacheForOffline && offline.IsEnabled)
            {
                await offline.StoreResponseAsync(cacheKey, json);
            }

            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null && cacheForOffline && offline.IsEnabled)
        {
            connection.ReportOffline();
            var cached = await offline.GetResponseAsync(cacheKey);
            if (cached is null)
            {
                throw;
            }
            return JsonSerializer.Deserialize<T>(cached, JsonOptions);
        }
    }

    /// <summary>
    /// Sends a change and returns the response body (or null). With <paramref name="queueIfOffline"/>, a change made
    /// while offline is queued and replayed later; the method then returns null.
    /// </summary>
    protected async Task<TResult?> SendAsync<TResult>(string client, HttpMethod method, string url, object? body, CancellationToken ct, bool queueIfOffline = false)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
        try
        {
            using var request = await CreateRequestAsync(method, url, json, ct);
            using var response = await httpClientFactory.CreateClient(client).SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }
            await EnsureSuccessAsync(response);
            connection.ReportOnline();

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return default;
            }

            return await response.Content.ReadFromJsonAsync<TResult>(JsonOptions, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null && queueIfOffline && offline.IsEnabled)
        {
            connection.ReportOffline();
            await offline.EnqueueAsync(new PendingRequest(client, method.Method, url, json, DateTimeOffset.UtcNow));
            return default;
        }
    }

    protected Task SendAsync(string client, HttpMethod method, string url, object? body, CancellationToken ct, bool queueIfOffline = false) =>
        SendAsync<JsonElement?>(client, method, url, body, ct, queueIfOffline);

    /// <summary>Replays a queued offline change. Used by the mobile app's sync service.</summary>
    public async Task<bool> ReplayAsync(PendingRequest pending, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(new HttpMethod(pending.Method), pending.Url, pending.Body, ct);
        using var response = await httpClientFactory.CreateClient(pending.Client).SendAsync(request, ct);

        // 4xx other than auth means the change no longer applies (e.g. the list was deleted); drop it.
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new SignInRequiredException();
        }
        return (int)response.StatusCode < 500;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, string? json, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        var token = await tokens.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (client.Address is { } address)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", address);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SignInRequiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail)}", null, response.StatusCode);
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>Whether the last API call reached the server; the layout shows an offline banner from it.</summary>
public sealed class ConnectionState
{
    public bool IsOffline { get; private set; }

    public event Action? Changed;

    public void ReportOnline() => Set(false);

    public void ReportOffline() => Set(true);

    private void Set(bool offline)
    {
        if (IsOffline != offline)
        {
            IsOffline = offline;
            Changed?.Invoke();
        }
    }
}

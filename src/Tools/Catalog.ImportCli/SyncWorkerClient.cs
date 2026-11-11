using System.Net;
using System.Net.Http.Json;

namespace Catalog.ImportCli;

// Mirrors Catalog.SyncWorker's BulkImportRequest / BulkImportStatus.
public record BulkImportRequest(IReadOnlyList<string>? Titles = null, IReadOnlyList<string>? Ids = null, int? Top = null);

public record BulkImportStatus(
    Guid Id,
    string State,
    string? Stage,
    int Total,
    int Processed,
    int Imported,
    int AlreadyInCatalog,
    int NotFound,
    int Failed,
    string? LastImported,
    IReadOnlyList<BulkImportProblem> Problems,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    string? Error)
{
    public bool IsRunning => State == "running";
}

public record BulkImportProblem(string Item, string Reason);

/// <summary>
/// The sync worker couldn't be reached or refused the request; the message is meant for the user.
/// </summary>
/// <summary>
/// Mirrors TvTracker.JobStatus: how a background job that adds Wikidata's data is doing.
/// </summary>
public record JobStatus(string Name, string State, bool Requested, int? Due, int Done, DateTime? LastRunAtUtc, string? LastError, string? Note);

public sealed class CliException(string message) : Exception(message);

/// <summary>
/// Calls the sync worker's internal bulk import endpoints (/api/v1/imports/bulk), signed in as the internal client
/// (tvtracker-internal, client credentials), which is all those endpoints accept.
/// </summary>
public sealed class SyncWorkerClient(Uri baseUrl, Uri keycloak, string clientSecret, string service = "the sync worker") : IDisposable
{
    private const string ClientId = "tvtracker-internal";
    private string Capitalised => char.ToUpperInvariant(service[0]) + service[1..];

    private readonly HttpClient _http = new() { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _tokenRefreshAt;

    /// <summary>
    /// Starts a job. Returns the running job instead, with Started false, when one is already running.
    /// </summary>
    public async Task<(bool Started, BulkImportStatus Status)> StartAsync(BulkImportRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsJsonAsync("api/v1/imports/bulk", request, ct));
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new CliException(await response.Content.ReadAsStringAsync(ct));
        }
        if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.Conflict))
        {
            throw Unexpected(response);
        }

        var status = await response.Content.ReadFromJsonAsync<BulkImportStatus>(ct) ?? throw Unexpected(response);
        return (response.StatusCode == HttpStatusCode.Accepted, status);
    }

    /// <summary>
    /// The running or most recent job, or null when there hasn't been one since the worker started.
    /// </summary>
    public async Task<BulkImportStatus?> GetCurrentAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync("api/v1/imports/bulk/current", ct));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Unexpected(response);
        }

        return await response.Content.ReadFromJsonAsync<BulkImportStatus>(ct);
    }

    /// <summary>
    /// False when the job isn't running any more.
    /// </summary>
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.DeleteAsync($"api/v1/imports/bulk/{id}", ct));
        return response.StatusCode == HttpStatusCode.Accepted;
    }

    /// <summary>
    /// The jobs a service lists at <paramref name="path"/>.
    /// </summary>
    public async Task<IReadOnlyList<JobStatus>> GetJobsAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync(path, ct));
        if (!response.IsSuccessStatusCode)
        {
            throw Unexpected(response);
        }
        return await response.Content.ReadFromJsonAsync<List<JobStatus>>(ct) ?? [];
    }

    /// <summary>
    /// Asks for a job to run now. Started is false when the job is off (its status says why).
    /// </summary>
    public async Task<(bool Started, JobStatus Status)> RunJobAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsync(path, null, ct));
        if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.Conflict))
        {
            throw Unexpected(response);
        }
        var status = await response.Content.ReadFromJsonAsync<JobStatus>(ct) ?? throw Unexpected(response);
        return (response.StatusCode == HttpStatusCode.Accepted, status);
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        await SignInAsync();
        try
        {
            var response = await send();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CliException($"{Capitalised} refused the internal client's token ({(int)response.StatusCode}). Check --client-secret.");
            }
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Couldn't reach {service} at {_http.BaseAddress} ({ex.Message}). Is the AppHost running?");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new CliException($"{Capitalised} at {_http.BaseAddress} didn't answer within {_http.Timeout.TotalSeconds:0} seconds. Is it still running?");
        }
    }

    /// <summary>
    /// A client-credentials token from Keycloak, kept until 80% of its lifetime (a followed import polls for a while).
    /// </summary>
    private async Task SignInAsync()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null && DateTimeOffset.UtcNow < _tokenRefreshAt)
        {
            return;
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(new Uri(keycloak, "realms/tvtracker/protocol/openid-connect/token"), new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = ClientId, ["client_secret"] = clientSecret }));
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Couldn't reach Keycloak at {keycloak} ({ex.Message}). Is the AppHost running?");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new CliException($"Keycloak refused the internal client ({(int)response.StatusCode}). Check --client-secret.");
            }
            var token = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                token?["access_token"]?.GetValue<string>() ?? throw new CliException("Keycloak answered without a token."));
            _tokenRefreshAt = DateTimeOffset.UtcNow.AddSeconds((token["expires_in"]?.GetValue<int>() ?? 60) * 0.8);
        }
    }

    private CliException Unexpected(HttpResponseMessage response) =>
        new($"{Capitalised} at {_http.BaseAddress} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
}

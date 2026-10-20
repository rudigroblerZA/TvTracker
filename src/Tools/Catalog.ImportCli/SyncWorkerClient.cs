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

/// <summary>The sync worker couldn't be reached or refused the request; the message is meant for the user.</summary>
public sealed class CliException(string message) : Exception(message);

/// <summary>Calls the sync worker's internal bulk import endpoints (/api/v1/imports/bulk).</summary>
public sealed class SyncWorkerClient(Uri baseUrl) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Starts a job. Returns the running job instead, with Started false, when one is already running.</summary>
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

    /// <summary>The running or most recent job, or null when there hasn't been one since the worker started.</summary>
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

    /// <summary>False when the job isn't running any more.</summary>
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.DeleteAsync($"api/v1/imports/bulk/{id}", ct));
        return response.StatusCode == HttpStatusCode.Accepted;
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Couldn't reach the sync worker at {_http.BaseAddress} ({ex.Message}). Is the AppHost running?");
        }
    }

    private CliException Unexpected(HttpResponseMessage response) =>
        new($"The sync worker at {_http.BaseAddress} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
}

using SQLite;
using TvTracker.UI.Services;

namespace TvTracker.Mobile.Services;

/// <summary>
/// Offline support in SQLite: the last response of each cached GET (Up Next, my shows, lists, show pages...)
/// and a queue of changes made while offline, replayed in order by <see cref="OfflineSyncService"/>.
/// </summary>
public sealed class SqliteOfflineStore : IOfflineStore
{
    private readonly Lazy<Task<SQLiteAsyncConnection>> _db = new(async () =>
    {
        var connection = new SQLiteAsyncConnection(Path.Combine(FileSystem.AppDataDirectory, "tvtracker-offline.db3"));
        await connection.CreateTableAsync<CachedResponse>();
        await connection.CreateTableAsync<QueuedRequest>();
        return connection;
    });

    public event Action? RequestQueued;

    public bool IsEnabled => true;

    public async Task StoreResponseAsync(string key, string json)
    {
        var db = await _db.Value;
        await db.InsertOrReplaceAsync(new CachedResponse { Key = key, Json = json, StoredAtUtc = DateTime.UtcNow });
    }

    public async Task<string?> GetResponseAsync(string key)
    {
        var db = await _db.Value;
        var row = await db.Table<CachedResponse>().Where(r => r.Key == key).FirstOrDefaultAsync();
        return row?.Json;
    }

    public async Task EnqueueAsync(PendingRequest request)
    {
        var db = await _db.Value;
        await db.InsertAsync(new QueuedRequest
        {
            Client = request.Client,
            Method = request.Method,
            Url = request.Url,
            Body = request.Body,
            CreatedAtUtc = request.CreatedAtUtc.UtcDateTime
        });
        RequestQueued?.Invoke();
    }

    public async Task<List<QueuedRequest>> GetQueueAsync()
    {
        var db = await _db.Value;
        return await db.Table<QueuedRequest>().OrderBy(r => r.Id).ToListAsync();
    }

    public async Task RemoveAsync(QueuedRequest request)
    {
        var db = await _db.Value;
        await db.DeleteAsync(request);
    }

    /// <summary>Clears cached data and pending changes (on sign-out).</summary>
    public async Task ClearAsync()
    {
        var db = await _db.Value;
        await db.DeleteAllAsync<CachedResponse>();
        await db.DeleteAllAsync<QueuedRequest>();
    }

    public sealed class CachedResponse
    {
        [PrimaryKey]
        public string Key { get; set; } = "";
        public string Json { get; set; } = "";
        public DateTime StoredAtUtc { get; set; }
    }

    public sealed class QueuedRequest
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Client { get; set; } = "";
        public string Method { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Body { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}

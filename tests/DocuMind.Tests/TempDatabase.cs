using DocuMind.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocuMind.Tests;

/// <summary>A throwaway on-disk SQLite database, so FTS5 behaves exactly like production.</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"documind-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_path};Pooling=False";

    public SqliteConnectionFactory Connections => new(ConnectionString);

    public async Task<DocumentStore> CreateStoreAsync()
    {
        var store = new DocumentStore(Connections);
        await store.InitializeAsync();
        return store;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }
}

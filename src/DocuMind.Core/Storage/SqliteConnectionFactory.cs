using Microsoft.Data.Sqlite;

namespace DocuMind.Core.Storage;

/// <summary>
/// Opens SQLite connections with foreign keys enforced and WAL journaling so reads don't block the writer.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!IsInMemory(dataSource) && Path.GetDirectoryName(Path.GetFullPath(dataSource)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private static bool IsInMemory(string dataSource) =>
        dataSource == ":memory:" || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}

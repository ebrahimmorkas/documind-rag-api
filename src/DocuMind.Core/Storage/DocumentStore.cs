using Microsoft.Data.Sqlite;
using DocuMind.Core.Ingestion;

namespace DocuMind.Core.Storage;

public sealed record DocumentInfo(Guid Id, string Name, string ContentType, long SizeBytes, int ChunkCount, DateTime CreatedAtUtc);

/// <summary>
/// SQLite persistence for documents and chunks. Chunk text is mirrored into an FTS5 virtual table
/// (kept in sync by triggers) that provides BM25-ranked full-text search.
/// </summary>
public sealed class DocumentStore(SqliteConnectionFactory connections)
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS documents (
            id              TEXT    PRIMARY KEY,
            name            TEXT    NOT NULL,
            content_type    TEXT    NOT NULL,
            size_bytes      INTEGER NOT NULL,
            sha256          TEXT    NOT NULL UNIQUE,
            chunk_count     INTEGER NOT NULL,
            created_at_utc  TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chunks (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id  TEXT    NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            chunk_index  INTEGER NOT NULL,
            page_number  INTEGER NULL,
            text         TEXT    NOT NULL,
            UNIQUE (document_id, chunk_index)
        );

        -- External-content FTS5 index: stores only the index, reads text from `chunks`.
        -- porter + unicode61 gives stemming ("running" matches "run") and accent folding.
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
            text,
            content = 'chunks',
            content_rowid = 'id',
            tokenize = 'porter unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
            INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
        END;

        CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
            INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ExistsByHashAsync(string sha256, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM documents WHERE sha256 = $sha256";
        command.Parameters.AddWithValue("$sha256", sha256);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task SaveAsync(DocumentInfo document, string sha256, IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertDocument = connection.CreateCommand())
        {
            insertDocument.Transaction = transaction;
            insertDocument.CommandText = """
                INSERT INTO documents (id, name, content_type, size_bytes, sha256, chunk_count, created_at_utc)
                VALUES ($id, $name, $contentType, $size, $sha256, $chunkCount, $createdAt)
                """;
            insertDocument.Parameters.AddWithValue("$id", document.Id.ToString());
            insertDocument.Parameters.AddWithValue("$name", document.Name);
            insertDocument.Parameters.AddWithValue("$contentType", document.ContentType);
            insertDocument.Parameters.AddWithValue("$size", document.SizeBytes);
            insertDocument.Parameters.AddWithValue("$sha256", sha256);
            insertDocument.Parameters.AddWithValue("$chunkCount", document.ChunkCount);
            insertDocument.Parameters.AddWithValue("$createdAt", document.CreatedAtUtc.ToString("O"));
            await insertDocument.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertChunk = connection.CreateCommand())
        {
            insertChunk.Transaction = transaction;
            insertChunk.CommandText = """
                INSERT INTO chunks (document_id, chunk_index, page_number, text)
                VALUES ($documentId, $index, $page, $text)
                """;
            var documentId = insertChunk.Parameters.AddWithValue("$documentId", document.Id.ToString());
            var index = insertChunk.Parameters.Add("$index", SqliteType.Integer);
            var page = insertChunk.Parameters.Add("$page", SqliteType.Integer);
            var text = insertChunk.Parameters.Add("$text", SqliteType.Text);

            // One prepared statement reused for every chunk.
            foreach (var chunk in chunks)
            {
                index.Value = chunk.Index;
                page.Value = (object?)chunk.PageNumber ?? DBNull.Value;
                text.Value = chunk.Text;
                await insertChunk.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentInfo>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, content_type, size_bytes, chunk_count, created_at_utc FROM documents ORDER BY created_at_utc DESC";

        var documents = new List<DocumentInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(Read(reader));
        }

        return documents;
    }

    public async Task<DocumentInfo?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, content_type, size_bytes, chunk_count, created_at_utc FROM documents WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DocumentInfo Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetInt32(4),
        DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind));
}

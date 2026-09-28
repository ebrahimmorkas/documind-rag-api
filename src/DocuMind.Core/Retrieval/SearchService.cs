using System.Text.RegularExpressions;
using DocuMind.Core.Common;
using DocuMind.Core.Storage;

namespace DocuMind.Core.Retrieval;

public sealed record SearchHit(
    long ChunkId,
    Guid DocumentId,
    string DocumentName,
    int ChunkIndex,
    int? PageNumber,
    string Text,
    string Snippet,
    double Score);

/// <summary>
/// Lexical retrieval with SQLite FTS5 and BM25 ranking. User input is reduced to plain quoted terms
/// joined with OR, so FTS5 query syntax (NEAR, column filters, quotes) can never be injected.
/// </summary>
public sealed partial class SearchService(SqliteConnectionFactory connections)
{
    public const int MaxResults = 20;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is",
        "it", "of", "on", "or", "our", "the", "to", "was", "we", "what", "when", "where", "which", "who", "why",
        "with", "you", "your"
    ];

    public async Task<Result<IReadOnlyList<SearchHit>>> SearchAsync(
        string query,
        int top = 5,
        Guid? documentId = null,
        CancellationToken cancellationToken = default)
    {
        var matchExpression = BuildMatchExpression(query);
        if (matchExpression is null)
        {
            return SearchErrors.EmptyQuery;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // bm25() returns lower-is-better scores; negate so larger means more relevant.
        command.CommandText = """
            SELECT c.id, c.document_id, d.name, c.chunk_index, c.page_number, c.text,
                   snippet(chunks_fts, 0, '**', '**', '…', 24) AS snippet,
                   -bm25(chunks_fts) AS score
            FROM chunks_fts
            JOIN chunks c ON c.id = chunks_fts.rowid
            JOIN documents d ON d.id = c.document_id
            WHERE chunks_fts MATCH $match
              AND ($documentId IS NULL OR c.document_id = $documentId)
            ORDER BY bm25(chunks_fts)
            LIMIT $top
            """;
        command.Parameters.AddWithValue("$match", matchExpression);
        command.Parameters.AddWithValue("$documentId", (object?)documentId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$top", Math.Clamp(top, 1, MaxResults));

        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                Math.Round(reader.GetDouble(7), 4)));
        }

        return hits;
    }

    /// <summary>Turns free text into a safe FTS5 expression, e.g. <c>What's the refund policy?</c> → <c>"refund" OR "policy"</c>.</summary>
    internal static string? BuildMatchExpression(string query)
    {
        var terms = Word().Matches(query.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct()
            .Take(32)
            .Select(t => $"\"{t}\"")
            .ToList();

        return terms.Count == 0 ? null : string.Join(" OR ", terms);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}

public static class SearchErrors
{
    public static readonly Error EmptyQuery =
        Error.Validation("Search.EmptyQuery", "Enter a question or some keywords to search for.");
}

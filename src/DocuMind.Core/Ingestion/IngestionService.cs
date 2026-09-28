using System.Security.Cryptography;
using DocuMind.Core.Common;
using DocuMind.Core.Storage;

namespace DocuMind.Core.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
}

/// <summary>
/// Turns an uploaded file into searchable chunks: validate → hash (duplicate check) → extract text →
/// chunk → persist chunks and full-text index in one transaction.
/// </summary>
public sealed class IngestionService(
    DocumentStore store,
    IEnumerable<ITextExtractor> extractors,
    TextChunker chunker,
    IngestionOptions options,
    TimeProvider timeProvider)
{
    public async Task<Result<DocumentInfo>> IngestAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var contentType = ContentTypes.FromFileName(fileName);
        var extractor = contentType is null ? null : extractors.FirstOrDefault(e => e.CanExtract(contentType));
        if (extractor is null)
        {
            return IngestionErrors.UnsupportedType(fileName);
        }

        // Buffer once: needed for hashing and because PDF parsing requires a seekable stream.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length == 0)
        {
            return IngestionErrors.Empty;
        }

        if (buffer.Length > options.MaxFileSizeBytes)
        {
            return IngestionErrors.TooLarge(options.MaxFileSizeBytes);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        if (await store.ExistsByHashAsync(sha256, cancellationToken))
        {
            return IngestionErrors.Duplicate(fileName);
        }

        buffer.Position = 0;
        IReadOnlyList<TextSection> sections;
        try
        {
            sections = extractor.Extract(buffer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return IngestionErrors.Unreadable(fileName);
        }

        var chunks = chunker.Chunk(sections);
        if (chunks.Count == 0)
        {
            return IngestionErrors.NoText(fileName);
        }

        var document = new DocumentInfo(
            Guid.CreateVersion7(),
            Path.GetFileName(fileName),
            contentType!,
            buffer.Length,
            chunks.Count,
            timeProvider.GetUtcNow().UtcDateTime);

        await store.SaveAsync(document, sha256, chunks, cancellationToken);
        return document;
    }
}

public static class IngestionErrors
{
    public static readonly Error Empty = Error.Validation("Document.Empty", "The uploaded file is empty.");

    public static Error UnsupportedType(string fileName) =>
        Error.Validation("Document.UnsupportedType", $"'{fileName}' is not supported. Upload .txt, .md or .pdf files.");

    public static Error TooLarge(long maxBytes) =>
        Error.Validation("Document.TooLarge", $"Files can be at most {maxBytes / 1024 / 1024} MB.");

    public static Error Duplicate(string fileName) =>
        Error.Conflict("Document.Duplicate", $"'{fileName}' has already been uploaded (identical content).");

    public static Error Unreadable(string fileName) =>
        Error.Validation("Document.Unreadable", $"'{fileName}' could not be read. Is it a valid, unencrypted file?");

    public static Error NoText(string fileName) =>
        Error.Validation("Document.NoText", $"No text could be extracted from '{fileName}'. Scanned PDFs need OCR first.");
}

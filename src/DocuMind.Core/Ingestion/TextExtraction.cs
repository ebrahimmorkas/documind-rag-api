using System.Text;
using UglyToad.PdfPig;

namespace DocuMind.Core.Ingestion;

/// <summary>A span of extracted text; <see cref="PageNumber"/> is set for paginated formats such as PDF.</summary>
public sealed record TextSection(string Text, int? PageNumber);

public interface ITextExtractor
{
    bool CanExtract(string contentType);

    IReadOnlyList<TextSection> Extract(Stream content);
}

public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly string[] SupportedTypes = ["text/plain", "text/markdown"];

    public bool CanExtract(string contentType) => SupportedTypes.Contains(contentType);

    public IReadOnlyList<TextSection> Extract(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return [new TextSection(reader.ReadToEnd(), null)];
    }
}

/// <summary>Extracts text page by page so answers can cite the page a passage came from.</summary>
public sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanExtract(string contentType) => contentType == "application/pdf";

    public IReadOnlyList<TextSection> Extract(Stream content)
    {
        using var pdf = PdfDocument.Open(content);

        return pdf.GetPages()
            .Select(page => new TextSection(string.Join(' ', page.GetWords().Select(w => w.Text)), page.Number))
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .ToList();
    }
}

public static class ContentTypes
{
    /// <summary>Maps a file name to a supported content type; browsers often send generic types for .md files.</summary>
    public static string? FromFileName(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        ".pdf" => "application/pdf",
        _ => null
    };
}

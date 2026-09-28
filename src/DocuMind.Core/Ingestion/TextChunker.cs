using System.Text;
using System.Text.RegularExpressions;

namespace DocuMind.Core.Ingestion;

public sealed record Chunk(int Index, string Text, int? PageNumber);

public sealed class ChunkingOptions
{
    /// <summary>Target maximum characters per chunk (~250-300 tokens), small enough for precise retrieval.</summary>
    public int MaxChunkLength { get; init; } = 1_200;

    /// <summary>Characters repeated from the end of the previous chunk so a sentence split across chunks stays findable.</summary>
    public int Overlap { get; init; } = 200;
}

/// <summary>
/// Paragraph-aware chunker: packs whole paragraphs into chunks, splitting oversized paragraphs on
/// sentence boundaries, and never merges text from different PDF pages (so citations keep a page number).
/// </summary>
public sealed partial class TextChunker(ChunkingOptions options)
{
    public TextChunker()
        : this(new ChunkingOptions())
    {
    }

    public IReadOnlyList<Chunk> Chunk(IReadOnlyList<TextSection> sections)
    {
        var chunks = new List<Chunk>();

        foreach (var section in sections)
        {
            var buffer = new StringBuilder();

            foreach (var piece in SplitIntoPieces(section.Text))
            {
                if (buffer.Length > 0 && buffer.Length + piece.Length + 1 > options.MaxChunkLength)
                {
                    var text = buffer.ToString().Trim();
                    chunks.Add(new Chunk(chunks.Count, text, section.PageNumber));

                    buffer.Clear();
                    buffer.Append(TailForOverlap(text)).Append(' ');
                }

                buffer.Append(piece).Append(' ');
            }

            var remaining = buffer.ToString().Trim();
            if (remaining.Length > 0 && (chunks.Count == 0 || !chunks[^1].Text.EndsWith(remaining, StringComparison.Ordinal)))
            {
                chunks.Add(new Chunk(chunks.Count, remaining, section.PageNumber));
            }
        }

        return chunks;
    }

    private IEnumerable<string> SplitIntoPieces(string text)
    {
        var paragraphs = ParagraphBreak().Split(text.Replace("\r\n", "\n"))
            .Select(p => Whitespace().Replace(p, " ").Trim())
            .Where(p => p.Length > 0);

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length <= options.MaxChunkLength)
            {
                yield return paragraph;
                continue;
            }

            foreach (var sentence in SentenceBoundary().Split(paragraph))
            {
                // A single enormous "sentence" (e.g. a table dump) is hard-wrapped.
                for (var start = 0; start < sentence.Length; start += options.MaxChunkLength)
                {
                    yield return sentence.Substring(start, Math.Min(options.MaxChunkLength, sentence.Length - start));
                }
            }
        }
    }

    private string TailForOverlap(string text)
    {
        if (options.Overlap <= 0 || text.Length <= options.Overlap)
        {
            return string.Empty;
        }

        var tail = text[^options.Overlap..];
        var firstSpace = tail.IndexOf(' ');
        return firstSpace >= 0 ? tail[(firstSpace + 1)..] : tail;
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBoundary();
}

using DocuMind.Core.Ingestion;

namespace DocuMind.Tests;

public class TextChunkerTests
{
    private static readonly TextChunker Chunker = new(new ChunkingOptions { MaxChunkLength = 100, Overlap = 20 });

    [Fact]
    public void Short_Text_Should_Produce_A_Single_Chunk()
    {
        var chunks = Chunker.Chunk([new TextSection("Hello world.\n\nSecond paragraph.", null)]);

        chunks.ShouldHaveSingleItem().Text.ShouldBe("Hello world. Second paragraph.");
    }

    [Fact]
    public void Chunks_Should_Respect_Max_Length_And_Keep_Order()
    {
        var paragraphs = Enumerable.Range(1, 10).Select(i => $"Paragraph number {i} talks about topic {i} in detail.");
        var chunks = Chunker.Chunk([new TextSection(string.Join("\n\n", paragraphs), null)]);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.Text.Length <= 100 + 20);
        chunks.Select(c => c.Index).ShouldBe(Enumerable.Range(0, chunks.Count));
        chunks[^1].Text.ShouldContain("Paragraph number 10");
    }

    [Fact]
    public void Consecutive_Chunks_Should_Overlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"Sentence {i} contains the keyword alpha{i} and more words."));
        var chunks = Chunker.Chunk([new TextSection(text, null)]);

        var tailWords = chunks[0].Text.Split(' ')[^2..];
        chunks[1].Text.ShouldContain(string.Join(' ', tailWords));
    }

    [Fact]
    public void Chunks_Should_Never_Span_Pdf_Pages()
    {
        var chunks = Chunker.Chunk([new TextSection("Page one text.", 1), new TextSection("Page two text.", 2)]);

        chunks.Select(c => c.PageNumber).ShouldBe([1, 2]);
    }

    [Fact]
    public void Huge_Paragraph_Should_Be_Split_On_Sentences()
    {
        var paragraph = string.Join(' ', Enumerable.Range(1, 20).Select(i => $"This is sentence {i}."));
        var chunks = Chunker.Chunk([new TextSection(paragraph, null)]);

        chunks.Count.ShouldBeGreaterThan(2);
        chunks.ShouldAllBe(c => c.Text.Length <= 120);
    }

    [Fact]
    public void Whitespace_Only_Text_Should_Produce_No_Chunks()
    {
        Chunker.Chunk([new TextSection("  \n\n \t ", null)]).ShouldBeEmpty();
    }
}

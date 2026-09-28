using System.Text;
using DocuMind.Core.Ingestion;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocuMind.Tests;

public sealed class IngestionServiceTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private IngestionService _service = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var store = await _db.CreateStoreAsync();
        _service = new IngestionService(
            store,
            [new PlainTextExtractor(), new PdfTextExtractor()],
            new TextChunker(),
            new IngestionOptions { MaxFileSizeBytes = 1024 * 1024 },
            TimeProvider.System);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private static MemoryStream Text(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Should_Ingest_Markdown_And_Report_Chunks()
    {
        var result = await _service.IngestAsync(Text("# Handbook\n\nEmployees get 25 days of paid leave."), "handbook.md", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ContentType.ShouldBe("text/markdown");
        result.Value.ChunkCount.ShouldBe(1);
    }

    [Fact]
    public async Task Should_Reject_Duplicate_Content_Even_With_Different_Name()
    {
        await _service.IngestAsync(Text("Same content"), "a.txt", Ct);

        (await _service.IngestAsync(Text("Same content"), "b.txt", Ct)).Error!.Code.ShouldBe("Document.Duplicate");
    }

    [Theory]
    [InlineData("image.png", "Document.UnsupportedType")]
    [InlineData("empty.txt", "Document.Empty")]
    public async Task Should_Validate_Files(string fileName, string expectedError)
    {
        var content = fileName == "empty.txt" ? new MemoryStream() : Text("data");

        (await _service.IngestAsync(content, fileName, Ct)).Error!.Code.ShouldBe(expectedError);
    }

    [Fact]
    public async Task Should_Reject_Files_Over_The_Size_Limit()
    {
        var big = Text(new string('x', 1024 * 1024 + 1));

        (await _service.IngestAsync(big, "big.txt", Ct)).Error!.Code.ShouldBe("Document.TooLarge");
    }

    [Fact]
    public async Task Should_Report_Invalid_Pdf_As_Unreadable()
    {
        (await _service.IngestAsync(Text("not really a pdf"), "broken.pdf", Ct)).Error!.Code.ShouldBe("Document.Unreadable");
    }

    [Fact]
    public void Should_Extract_Pdf_Text_Per_Page()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText("Refund policy: returns within 30 days.", 12, new(50, 780), font);
        builder.AddPage(595, 842).AddText("Shipping is free above 50 euros.", 12, new(50, 780), font);

        var sections = new PdfTextExtractor().Extract(new MemoryStream(builder.Build()));

        sections.Select(s => s.PageNumber).ShouldBe([1, 2]);
        sections[1].Text.ShouldContain("Shipping");
    }
}

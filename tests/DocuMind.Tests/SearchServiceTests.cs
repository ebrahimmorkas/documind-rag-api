using System.Text;
using DocuMind.Core.Ingestion;
using DocuMind.Core.Retrieval;
using DocuMind.Core.Storage;

namespace DocuMind.Tests;

public sealed class SearchServiceTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private IngestionService _ingestion = null!;
    private SearchService _search = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var store = await _db.CreateStoreAsync();
        _ingestion = new IngestionService(store, [new PlainTextExtractor()], new TextChunker(), new IngestionOptions(), TimeProvider.System);
        _search = new SearchService(_db.Connections);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private async Task<DocumentInfo> AddAsync(string name, string text) =>
        (await _ingestion.IngestAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), name, Ct)).Value;

    [Fact]
    public async Task Should_Rank_The_Most_Relevant_Chunk_First()
    {
        await AddAsync("shipping.md", "Shipping takes three to five business days. Express shipping is available.");
        await AddAsync("refunds.md", "Refunds are issued within 14 days. To request a refund, contact support. Refund requests need an order number.");

        var hits = (await _search.SearchAsync("How do I get a refund?", cancellationToken: Ct)).Value;

        hits.First().DocumentName.ShouldBe("refunds.md");
        hits.Select(h => h.Score).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task Should_Match_Word_Variants_Through_Stemming()
    {
        await AddAsync("policy.txt", "Employees running late must notify their manager.");

        var hits = (await _search.SearchAsync("run", cancellationToken: Ct)).Value;

        hits.ShouldHaveSingleItem().Snippet.ShouldContain("**running**");
    }

    [Fact]
    public async Task Should_Filter_By_Document()
    {
        var first = await AddAsync("a.txt", "Budget approval requires two signatures.");
        await AddAsync("b.txt", "Budget planning happens every quarter.");

        var hits = (await _search.SearchAsync("budget", documentId: first.Id, cancellationToken: Ct)).Value;

        hits.ShouldHaveSingleItem().DocumentId.ShouldBe(first.Id);
    }

    [Theory]
    [InlineData("\"budget approval")]
    [InlineData("NEAR(budget approval) AND text:")]
    [InlineData("budget* OR ) (")]
    public async Task Should_Treat_Fts_Syntax_As_Plain_Text(string query)
    {
        await AddAsync("a.txt", "Budget approval requires two signatures.");

        var result = await _search.SearchAsync(query, cancellationToken: Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("the and of")]
    [InlineData("?!")]
    public async Task Should_Reject_Queries_Without_Meaningful_Terms(string query)
    {
        (await _search.SearchAsync(query, cancellationToken: Ct)).Error.ShouldBe(SearchErrors.EmptyQuery);
    }

    [Fact]
    public async Task Deleted_Documents_Should_Disappear_From_The_Index()
    {
        var document = await AddAsync("temp.txt", "Temporary secret launch codename is Falcon.");
        await new DocumentStore(_db.Connections).DeleteAsync(document.Id, Ct);

        (await _search.SearchAsync("falcon", cancellationToken: Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public void BuildMatchExpression_Should_Quote_Terms_And_Drop_Stop_Words()
    {
        SearchService.BuildMatchExpression("What's the refund POLICY?").ShouldBe("\"refund\" OR \"policy\"");
    }
}

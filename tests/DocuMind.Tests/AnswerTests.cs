using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using DocuMind.Core.Answering;
using DocuMind.Core.Common;
using DocuMind.Core.Ingestion;
using DocuMind.Core.Retrieval;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuMind.Tests;

public sealed class AnswerTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private readonly FakeGenerator _generator = new();
    private IngestionService _ingestion = null!;
    private AnswerService _answers = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var store = await _db.CreateStoreAsync();
        _ingestion = new IngestionService(store, [new PlainTextExtractor()], new TextChunker(), new IngestionOptions(), TimeProvider.System);
        _answers = new AnswerService(new SearchService(_db.Connections), _generator);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private Task AddAsync(string name, string text) =>
        _ingestion.IngestAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), name, Ct);

    [Fact]
    public async Task Should_Not_Call_The_Model_When_Nothing_Relevant_Is_Found()
    {
        await AddAsync("hr.md", "Employees receive 25 days of paid vacation per year.");

        var answer = (await _answers.AskAsync("What is the office wifi password?", cancellationToken: Ct)).Value;

        answer.Grounded.ShouldBeFalse();
        answer.Text.ShouldBe(AnswerService.NotFoundMessage);
        _generator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Pass_Retrieved_Passages_And_Number_The_Sources()
    {
        await AddAsync("hr.md", "Employees receive 25 days of paid vacation per year.");
        _generator.Segments = [new AnswerSegment("You get 25 vacation days.", [new AnswerCitation(1, "25 days of paid vacation")])];

        var answer = (await _answers.AskAsync("How many vacation days do employees get?", cancellationToken: Ct)).Value;

        _generator.LastPassages.ShouldHaveSingleItem().DocumentName.ShouldBe("hr.md");
        answer.Sources.ShouldHaveSingleItem().Number.ShouldBe(1);
        answer.Grounded.ShouldBeTrue();
        answer.Text.ShouldBe("You get 25 vacation days.");
    }

    [Fact]
    public async Task Should_Surface_Generator_Errors()
    {
        await AddAsync("hr.md", "Employees receive 25 days of paid vacation per year.");
        _generator.Error = AnswerErrors.ModelUnavailable;

        (await _answers.AskAsync("vacation days", cancellationToken: Ct)).Error.ShouldBe(AnswerErrors.ModelUnavailable);
    }

    [Fact]
    public async Task Should_Reject_Overly_Long_Questions()
    {
        (await _answers.AskAsync(new string('a', AnswerService.MaxQuestionLength + 1), cancellationToken: Ct)).Error.ShouldBe(AnswerErrors.QuestionTooLong);
    }

    [Fact]
    public async Task Stream_Should_Emit_Sources_Then_Model_Events_Then_Done()
    {
        await AddAsync("hr.md", "Employees receive 25 days of paid vacation per year.");

        var events = new List<AnswerStreamEvent>();
        await foreach (var e in _answers.StreamAsync("vacation days", cancellationToken: Ct))
        {
            events.Add(e);
        }

        events.Select(e => e.Type).ShouldBe([
            AnswerStreamEventType.Sources,
            AnswerStreamEventType.Text,
            AnswerStreamEventType.Citation,
            AnswerStreamEventType.Done
        ]);
        events[0].Sources.ShouldNotBeNull().ShouldHaveSingleItem();
    }

    [Fact]
    public void ClaudeRequest_Should_Send_Passages_As_Cited_Documents_Before_The_Question()
    {
        var generator = new ClaudeAnswerGenerator(
            new AnthropicClient { ApiKey = "test-key" },
            new ClaudeOptions(),
            NullLogger<ClaudeAnswerGenerator>.Instance);

        var request = generator.BuildRequest("What is the refund window?",
        [
            new SearchHit(1, Guid.NewGuid(), "policy.pdf", 0, 3, "Refunds within 30 days.", "", 1),
            new SearchHit(2, Guid.NewGuid(), "faq.md", 0, null, "Contact support for refunds.", "", 0.5)
        ]);

        request.Model.ToString().ShouldContain("claude-opus-5");
        request.System!.ToString().ShouldContain("Use only the information in the provided documents");

        var content = request.Messages.ShouldHaveSingleItem().Content.ToString();
        content.ShouldContain("policy.pdf (page 3)");
        content.ShouldContain("\"citations\"");
        content.IndexOf("Refunds within 30 days", StringComparison.Ordinal)
            .ShouldBeLessThan(content.IndexOf("What is the refund window?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ask_Endpoint_Should_Return_503_When_No_Api_Key_Is_Configured()
    {
        var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            await AddAsync("hr.md", "Employees receive 25 days of paid vacation per year.");
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Documents", _db.ConnectionString);
                builder.UseSetting("Anthropic:ApiKey", "");
            });

            var response = await factory.CreateClient().PostAsJsonAsync("/api/ask", new { question = "How many vacation days?" }, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Answer.NotConfigured");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        }
    }

    private sealed class FakeGenerator : IAnswerGenerator
    {
        public int Calls { get; private set; }

        public IReadOnlyList<SearchHit> LastPassages { get; private set; } = [];

        public IReadOnlyList<AnswerSegment> Segments { get; set; } = [];

        public Error? Error { get; set; }

        public Task<Result<IReadOnlyList<AnswerSegment>>> GenerateAsync(string question, IReadOnlyList<SearchHit> passages, CancellationToken cancellationToken)
        {
            Calls++;
            LastPassages = passages;
            return Task.FromResult(Error is null ? (Result<IReadOnlyList<AnswerSegment>>)Segments.ToList() : Error);
        }

        public async IAsyncEnumerable<AnswerStreamEvent> StreamAsync(
            string question,
            IReadOnlyList<SearchHit> passages,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new AnswerStreamEvent(AnswerStreamEventType.Text, "You get 25 days.");
            yield return new AnswerStreamEvent(AnswerStreamEventType.Citation, Citation: new AnswerCitation(1, "25 days"));
        }
    }
}

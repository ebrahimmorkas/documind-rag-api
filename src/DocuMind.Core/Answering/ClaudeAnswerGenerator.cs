using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using DocuMind.Core.Common;
using DocuMind.Core.Retrieval;
using Microsoft.Extensions.Logging;

namespace DocuMind.Core.Answering;

public sealed class ClaudeOptions
{
    public const string SectionName = "Anthropic";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = "claude-opus-5";

    public int MaxTokens { get; init; } = 16_000;
}

/// <summary>
/// Answers questions with Claude using the retrieved passages as <c>document</c> blocks with citations
/// enabled, so every claim in the answer comes back linked to the exact passage text that supports it.
/// </summary>
public sealed partial class ClaudeAnswerGenerator(
    AnthropicClient client,
    ClaudeOptions options,
    ILogger<ClaudeAnswerGenerator> logger)
    : IAnswerGenerator
{
    // Server-side fallback: if the primary model declines a request, the API re-serves it with a
    // suitable fallback model inside the same call.
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    internal const string SystemPrompt = """
        You answer questions about the user's documents.
        Use only the information in the provided documents. If they don't contain the answer, say that
        you couldn't find it in the documents instead of guessing, and don't use outside knowledge.
        Be concise and direct. Answer in the language of the question.
        """;

    public async Task<Result<IReadOnlyList<AnswerSegment>>> GenerateAsync(
        string question,
        IReadOnlyList<SearchHit> passages,
        CancellationToken cancellationToken)
    {
        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(BuildRequest(question, passages), cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            LogModelError(logger, ex);
            return AnswerErrors.ModelUnavailable;
        }

        if (response.StopReason == "refusal")
        {
            return AnswerErrors.Declined;
        }

        var segments = new List<AnswerSegment>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out BetaTextBlock? text))
            {
                segments.Add(new AnswerSegment(text.Text, MapCitations(text.Citations)));
            }
        }

        return segments;
    }

    public async IAsyncEnumerable<AnswerStreamEvent> StreamAsync(
        string question,
        IReadOnlyList<SearchHit> passages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var events = client.Beta.Messages.CreateStreaming(BuildRequest(question, passages), cancellationToken);
        await using var enumerator = events.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            AnswerStreamEvent? next = null;
            var finished = false;

            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    finished = true;
                }
                else
                {
                    next = Map(enumerator.Current);
                }
            }
            catch (AnthropicApiException ex)
            {
                LogModelError(logger, ex);
                next = new AnswerStreamEvent(AnswerStreamEventType.Error, AnswerErrors.ModelUnavailable.Message);
                finished = true;
            }

            if (next is not null)
            {
                yield return next;
            }

            if (finished)
            {
                yield break;
            }
        }
    }

    internal MessageCreateParams BuildRequest(string question, IReadOnlyList<SearchHit> passages)
    {
        var content = new List<BetaContentBlockParam>();

        foreach (var passage in passages)
        {
            content.Add(new BetaRequestDocumentBlock
            {
                Source = new BetaPlainTextSource { Data = passage.Text },
                Title = passage.PageNumber is { } page ? $"{passage.DocumentName} (page {page})" : passage.DocumentName,
                Citations = new BetaCitationsConfigParam { Enabled = true },
            });
        }

        content.Add(new BetaTextBlockParam { Text = question });

        return new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
            System = SystemPrompt,
            Messages = [new BetaMessageParam { Role = Role.User, Content = content }],
        };
    }

    private static AnswerStreamEvent? Map(BetaRawMessageStreamEvent streamEvent)
    {
        if (!streamEvent.TryPickContentBlockDelta(out var delta))
        {
            return null;
        }

        if (delta.Delta.TryPickText(out var text))
        {
            return new AnswerStreamEvent(AnswerStreamEventType.Text, text.Text);
        }

        if (delta.Delta.TryPickCitations(out var citationDelta) && MapStreamedCitation(citationDelta.Citation) is { } citation)
        {
            return new AnswerStreamEvent(AnswerStreamEventType.Citation, Citation: citation);
        }

        return null;
    }

    private static IReadOnlyList<AnswerCitation> MapCitations(IReadOnlyList<BetaTextCitation>? citations) =>
        citations?.Select(MapCitation).OfType<AnswerCitation>().ToList() ?? [];

    // Documents are plain text, so citations are character locations; document indexes are 0-based
    // in the order the passages were sent, which is the same order as the numbered sources.
    private static AnswerCitation? MapCitation(BetaTextCitation citation) =>
        citation.TryPickCitationCharLocation(out var location)
            ? new AnswerCitation((int)location.DocumentIndex + 1, location.CitedText)
            : null;

    private static AnswerCitation? MapStreamedCitation(Citation citation) =>
        citation.TryPickBetaCitationCharLocation(out var location)
            ? new AnswerCitation((int)location.DocumentIndex + 1, location.CitedText)
            : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Claude request failed")]
    private static partial void LogModelError(ILogger logger, Exception exception);
}

/// <summary>Used when no API key is configured: search keeps working, answering reports why it can't.</summary>
public sealed class UnconfiguredAnswerGenerator : IAnswerGenerator
{
    public Task<Result<IReadOnlyList<AnswerSegment>>> GenerateAsync(string question, IReadOnlyList<SearchHit> passages, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<AnswerSegment>>>(AnswerErrors.NotConfigured);

    public async IAsyncEnumerable<AnswerStreamEvent> StreamAsync(
        string question,
        IReadOnlyList<SearchHit> passages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new AnswerStreamEvent(AnswerStreamEventType.Error, AnswerErrors.NotConfigured.Message);
    }
}

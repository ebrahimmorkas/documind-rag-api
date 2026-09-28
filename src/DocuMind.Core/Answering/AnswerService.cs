using System.Runtime.CompilerServices;
using DocuMind.Core.Common;
using DocuMind.Core.Retrieval;

namespace DocuMind.Core.Answering;

/// <summary>
/// Retrieval-augmented generation: find the most relevant passages with BM25, then let the model answer
/// from those passages only. If nothing relevant is found, the model is not called at all.
/// </summary>
public sealed class AnswerService(SearchService search, IAnswerGenerator generator)
{
    public const int MaxQuestionLength = 1_000;
    public const int DefaultPassages = 6;

    internal const string NotFoundMessage = "I couldn't find anything about that in your documents.";

    public async Task<Result<Answer>> AskAsync(
        string question,
        int passages = DefaultPassages,
        Guid? documentId = null,
        CancellationToken cancellationToken = default)
    {
        var retrieval = await RetrieveAsync(question, passages, documentId, cancellationToken);
        if (retrieval.IsFailure)
        {
            return retrieval.Error!;
        }

        var hits = retrieval.Value;
        if (hits.Count == 0)
        {
            return new Answer(question, NotFoundMessage, [new AnswerSegment(NotFoundMessage, [])], [], Grounded: false);
        }

        var generated = await generator.GenerateAsync(question, hits, cancellationToken);
        if (generated.IsFailure)
        {
            return generated.Error!;
        }

        var segments = generated.Value;
        return new Answer(
            question,
            string.Concat(segments.Select(s => s.Text)).Trim(),
            segments,
            hits.Select((hit, i) => AnswerSource.From(i + 1, hit)).ToList(),
            Grounded: segments.Any(s => s.Citations.Count > 0));
    }

    public async IAsyncEnumerable<AnswerStreamEvent> StreamAsync(
        string question,
        int passages = DefaultPassages,
        Guid? documentId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var retrieval = await RetrieveAsync(question, passages, documentId, cancellationToken);
        if (retrieval.IsFailure)
        {
            yield return new AnswerStreamEvent(AnswerStreamEventType.Error, retrieval.Error!.Message);
            yield break;
        }

        var hits = retrieval.Value;
        yield return new AnswerStreamEvent(
            AnswerStreamEventType.Sources,
            Sources: hits.Select((hit, i) => AnswerSource.From(i + 1, hit)).ToList());

        if (hits.Count == 0)
        {
            yield return new AnswerStreamEvent(AnswerStreamEventType.Text, NotFoundMessage);
        }
        else
        {
            await foreach (var streamEvent in generator.StreamAsync(question, hits, cancellationToken))
            {
                yield return streamEvent;
                if (streamEvent.Type == AnswerStreamEventType.Error)
                {
                    yield break;
                }
            }
        }

        yield return new AnswerStreamEvent(AnswerStreamEventType.Done);
    }

    private async Task<Result<IReadOnlyList<SearchHit>>> RetrieveAsync(
        string question,
        int passages,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        if (question.Length > MaxQuestionLength)
        {
            return AnswerErrors.QuestionTooLong;
        }

        return await search.SearchAsync(question, Math.Clamp(passages, 1, 10), documentId, cancellationToken);
    }
}

using DocuMind.Core.Common;
using DocuMind.Core.Retrieval;

namespace DocuMind.Core.Answering;

/// <summary>A retrieved passage shown to the model, numbered the way the answer cites it ([1], [2], …).</summary>
public sealed record AnswerSource(int Number, Guid DocumentId, string DocumentName, int? PageNumber, int ChunkIndex, string Snippet)
{
    public static AnswerSource From(int number, SearchHit hit) =>
        new(number, hit.DocumentId, hit.DocumentName, hit.PageNumber, hit.ChunkIndex, hit.Snippet);
}

/// <summary>A verbatim quote from a source that supports a piece of the answer.</summary>
public sealed record AnswerCitation(int SourceNumber, string CitedText);

/// <summary>A span of answer text together with the citations that back it.</summary>
public sealed record AnswerSegment(string Text, IReadOnlyList<AnswerCitation> Citations);

public sealed record Answer(
    string Question,
    string Text,
    IReadOnlyList<AnswerSegment> Segments,
    IReadOnlyList<AnswerSource> Sources,
    bool Grounded);

public enum AnswerStreamEventType
{
    Sources,
    Text,
    Citation,
    Done,
    Error
}

public sealed record AnswerStreamEvent(
    AnswerStreamEventType Type,
    string? Text = null,
    AnswerCitation? Citation = null,
    IReadOnlyList<AnswerSource>? Sources = null);

/// <summary>Generates an answer that is grounded in the given passages.</summary>
public interface IAnswerGenerator
{
    Task<Result<IReadOnlyList<AnswerSegment>>> GenerateAsync(
        string question,
        IReadOnlyList<SearchHit> passages,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AnswerStreamEvent> StreamAsync(
        string question,
        IReadOnlyList<SearchHit> passages,
        CancellationToken cancellationToken);
}

public static class AnswerErrors
{
    public static readonly Error NotConfigured = Error.Unavailable(
        "Answer.NotConfigured",
        "AI answers are not configured. Set Anthropic:ApiKey (or ANTHROPIC_API_KEY); search still works without it.");

    public static readonly Error ModelUnavailable = Error.Unavailable(
        "Answer.ModelUnavailable",
        "The answering model is temporarily unavailable. Please try again shortly.");

    public static readonly Error Declined = Error.Validation(
        "Answer.Declined",
        "The model declined to answer this question.");

    public static readonly Error QuestionTooLong = Error.Validation(
        "Answer.QuestionTooLong",
        "Questions can be at most 1000 characters.");
}

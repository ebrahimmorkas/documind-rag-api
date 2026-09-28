using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DocuMind.Core.Answering;

namespace DocuMind.Api.Endpoints;

internal static class AskEndpoints
{
    public sealed record AskRequest(string Question, int? Passages, Guid? DocumentId);

    public static void MapAskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ask").WithTags("Ask");

        group.MapPost("/", AskAsync)
            .WithName("Ask")
            .WithSummary("Answers a question from the indexed documents, with citations")
            .Produces<Answer>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/stream", Stream)
            .WithName("AskStream")
            .WithSummary("Streams the answer as Server-Sent Events: sources, text, citation, done, error");
    }

    private static async Task<IResult> AskAsync(AskRequest request, AnswerService answers, CancellationToken cancellationToken)
    {
        var result = await answers.AskAsync(
            request.Question ?? string.Empty,
            request.Passages ?? AnswerService.DefaultPassages,
            request.DocumentId,
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static IResult Stream(AskRequest request, AnswerService answers, CancellationToken cancellationToken) =>
        TypedResults.ServerSentEvents(ToSse(
            answers.StreamAsync(request.Question ?? string.Empty, request.Passages ?? AnswerService.DefaultPassages, request.DocumentId, cancellationToken),
            cancellationToken));

    private static async IAsyncEnumerable<SseItem<AnswerStreamEvent>> ToSse(
        IAsyncEnumerable<AnswerStreamEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in events.WithCancellation(cancellationToken))
        {
            yield return new SseItem<AnswerStreamEvent>(streamEvent, streamEvent.Type.ToString().ToLowerInvariant());
        }
    }
}

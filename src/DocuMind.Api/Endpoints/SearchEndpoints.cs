using DocuMind.Core.Retrieval;

namespace DocuMind.Api.Endpoints;

internal static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/search", SearchAsync)
            .WithTags("Search")
            .WithName("Search")
            .WithSummary("BM25 keyword search over all indexed chunks")
            .Produces<IReadOnlyList<SearchHit>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

    private static async Task<IResult> SearchAsync(
        string q,
        SearchService search,
        CancellationToken cancellationToken,
        int top = 5,
        Guid? documentId = null)
    {
        var result = await search.SearchAsync(q, top, documentId, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }
}

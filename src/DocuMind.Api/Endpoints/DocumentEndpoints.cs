using DocuMind.Core.Ingestion;
using DocuMind.Core.Storage;

namespace DocuMind.Api.Endpoints;

internal static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        group.MapPost("/", UploadAsync)
            .WithName("UploadDocument")
            .WithSummary("Uploads a .txt, .md or .pdf file and indexes it for search")
            .DisableAntiforgery()
            .Produces<DocumentInfo>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", async (DocumentStore store, CancellationToken ct) => TypedResults.Ok(await store.ListAsync(ct)))
            .WithName("ListDocuments")
            .WithSummary("Lists indexed documents");

        group.MapGet("/{id:guid}", async (Guid id, DocumentStore store, CancellationToken ct) =>
                await store.GetAsync(id, ct) is { } document
                    ? Results.Ok(document)
                    : Results.Problem($"Document '{id}' was not found.", title: "Document.NotFound", statusCode: StatusCodes.Status404NotFound))
            .WithName("GetDocument")
            .WithSummary("Gets a document's metadata")
            .Produces<DocumentInfo>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (Guid id, DocumentStore store, CancellationToken ct) =>
                await store.DeleteAsync(id, ct)
                    ? Results.NoContent()
                    : Results.Problem($"Document '{id}' was not found.", title: "Document.NotFound", statusCode: StatusCodes.Status404NotFound))
            .WithName("DeleteDocument")
            .WithSummary("Deletes a document and removes it from the index")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> UploadAsync(IFormFile file, IngestionService ingestion, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var result = await ingestion.IngestAsync(stream, file.FileName, cancellationToken);

        return result.IsSuccess
            ? TypedResults.Created($"/api/documents/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }
}

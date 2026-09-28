using DocuMind.Core.Common;

namespace DocuMind.Api.Endpoints;

internal static class ResultExtensions
{
    public static IResult ToProblem(this Error error) => TypedResults.Problem(
        detail: error.Message,
        title: error.Code,
        statusCode: error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        });
}

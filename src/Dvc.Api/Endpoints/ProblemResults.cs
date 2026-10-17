using Microsoft.AspNetCore.Http.HttpResults;

namespace Dvc.Api.Endpoints;

// Every error an endpoint returns is a problem details response (RFC 9457),
// with the message in "detail".
public static class ProblemResults
{
    public static ProblemHttpResult Problem(int statusCode, string detail) =>
        TypedResults.Problem(detail, statusCode: statusCode);

    public static ProblemHttpResult BadRequest(string detail) =>
        Problem(StatusCodes.Status400BadRequest, detail);
}

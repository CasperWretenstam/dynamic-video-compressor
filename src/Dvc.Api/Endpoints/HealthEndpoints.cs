using Microsoft.AspNetCore.Http.HttpResults;

namespace Dvc.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", GetHealth)
            .WithName("GetHealth")
            .WithTags("Health");

        return app;
    }

    // Liveness only: confirms the process responds, without checking the
    // database or storage. Used by the Docker HEALTHCHECK.
    /// <summary>Check that the service is running</summary>
    /// <remarks>
    /// Responds while the service is up and handling requests. It doesn't
    /// check the database or storage, so it says the service is reachable, not
    /// that jobs will succeed.
    /// </remarks>
    /// <response code="200">The service is running.</response>
    internal static Ok GetHealth() => TypedResults.Ok();
}

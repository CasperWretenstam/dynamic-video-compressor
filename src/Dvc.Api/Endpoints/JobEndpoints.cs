using Dvc.Api.Infrastructure.Data;
using Dvc.Api.Infrastructure.OpenApi;
using Dvc.Api.Models.DTOs.Responses;
using Dvc.Api.Models.Entities;
using Dvc.Api.Models.Options;
using Dvc.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Dvc.Api.Endpoints.ProblemResults;

namespace Dvc.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs").WithTags("Jobs");

        group.MapPost("/", SubmitJob)
            .WithName("SubmitJob")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .AddOpenApiOperationTransformer((operation, context, _) =>
            {
                // The body is parsed by hand, so the form is described from
                // the same parameter descriptions GET /v1/options returns.
                var encodingOptions = context.ApplicationServices.GetRequiredService<IOptions<EncodingOptions>>().Value;
                operation.RequestBody = JobFormSchema.Build(OptionsEndpoints.BuildJobOptions(encodingOptions));
                return Task.CompletedTask;
            });

        group.MapGet("/{id:guid}", GetJobStatus)
            .WithName("GetJobStatus")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/result", GetJobResult)
            .WithName("GetJobResult")
            .Produces<Stream>(StatusCodes.Status200OK, "video/mp4")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "video/mp4")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteJob)
            .WithName("DeleteJob")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Results<AcceptedAtRoute<JobSubmittedResponse>, ProblemHttpResult>> SubmitJob(
        HttpRequest httpRequest,
        IJobSubmissionService jobSubmissionService,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var inputDirectory = configuration["Storage:InputDirectory"] ?? "Storage/Input";
        var upload = await JobUploadReader.ReadAsync(httpRequest, inputDirectory, cancellationToken);
        if (!upload.Succeeded)
        {
            return upload.Problem;
        }

        var submitted = false;
        try
        {
            var job = await jobSubmissionService.SubmitAsync(upload.Request, cancellationToken);
            submitted = true;

            // By route name, so the Location header includes the group prefix.
            return TypedResults.AcceptedAtRoute(JobSubmittedResponse.FromJob(job), "GetJobStatus", new { id = job.Id });
        }
        finally
        {
            // Without a queued job, nothing else will consume the upload.
            if (!submitted)
            {
                File.Delete(upload.Request.InputPath);
            }
        }
    }

    private static async Task<Results<Ok<JobStatusResponse>, ProblemHttpResult>> GetJobStatus(Guid id, DvcDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FindAsync([id], cancellationToken);

        if (job is null)
        {
            return JobNotFound(id);
        }

        return TypedResults.Ok(JobStatusResponse.FromJob(job));
    }

    private static async Task<Results<PhysicalFileHttpResult, ProblemHttpResult>> GetJobResult(Guid id, DvcDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FindAsync([id], cancellationToken);

        if (job is null)
        {
            return JobNotFound(id);
        }

        if (job.Status != JobStatus.Completed || job.OutputPath is null)
        {
            return Problem(StatusCodes.Status409Conflict, $"Job is not ready yet (status: {job.Status}).");
        }

        return TypedResults.PhysicalFile(job.OutputPath, "video/mp4", enableRangeProcessing: true);
    }

    // Deleting a queued job cancels it, since the worker skips IDs whose row
    // is gone. A processing job is refused: an encode can't be stopped
    // partway.
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteJob(
        Guid id,
        DvcDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Deletes only if the status is still the one just read, so it can't
        // race the worker claiming the job; otherwise it re-reads. Status
        // only moves forward and processing returns, so the loop ends.
        while (true)
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

            if (job is null)
            {
                return JobNotFound(id);
            }

            if (job.Status == JobStatus.Processing)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "Job is being processed and can't be deleted until it finishes.");
            }

            var deleted = await db.Jobs
                .Where(j => j.Id == id && j.Status == job.Status)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted == 0)
            {
                continue;
            }

            // The row is gone, so a file that can't be deleted (e.g. a result
            // still being streamed, on Windows) is logged rather than failing
            // the request. An input the worker already deleted is fine:
            // File.Delete ignores missing files.
            var logger = loggerFactory.CreateLogger(typeof(JobEndpoints).FullName!);
            TryDeleteFile(job.InputPath, id, logger);
            if (job.OutputPath is not null)
            {
                TryDeleteFile(job.OutputPath, id, logger);
            }

            logger.LogInformation("Deleted job {JobId} ({Status})", id, job.Status);
            return TypedResults.NoContent();
        }
    }

    private static ProblemHttpResult JobNotFound(Guid id) =>
        Problem(StatusCodes.Status404NotFound, $"No job with ID {id}.");

    private static void TryDeleteFile(string path, Guid jobId, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete file for job {JobId} at {Path}", jobId, path);
        }
    }
}

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

    /// <summary>Submit a video for compression</summary>
    /// <remarks>
    /// Uploads the video and its settings as <c>multipart/form-data</c> and
    /// queues a job, responding straight away with its ID rather than waiting
    /// for the encode. Poll <c>GET /v1/jobs/{id}</c> until the job's status is
    /// <c>Completed</c> or <c>Failed</c>, then download the result from
    /// <c>GET /v1/jobs/{id}/result</c>. Jobs are encoded one at a time, in the
    /// order they were submitted.
    /// </remarks>
    /// <response code="202">The job is queued. The <c>Location</c> header points to its status.</response>
    /// <response code="400">
    /// A field or the file was rejected, e.g. an unknown or repeated field, an
    /// invalid value, an unsupported file type or no file at all. No job was
    /// created.
    /// </response>
    /// <response code="413">The upload is larger than the 2GB limit. No job was created.</response>
    internal static async Task<Results<AcceptedAtRoute<JobSubmittedResponse>, ProblemHttpResult>> SubmitJob(
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

    /// <summary>Get a job's status</summary>
    /// <remarks>
    /// Poll this after submitting until <c>status</c> is <c>Completed</c> or
    /// <c>Failed</c>. Once processing has analysed the video, <c>output</c>
    /// shows the dimensions and framerate it's being encoded to.
    /// </remarks>
    /// <param name="id">The job's ID, as returned when it was submitted.</param>
    /// <response code="200">The job's status, settings and output.</response>
    /// <response code="404">There's no job with this ID, or it has been deleted.</response>
    internal static async Task<Results<Ok<JobStatusResponse>, ProblemHttpResult>> GetJobStatus(Guid id, DvcDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FindAsync([id], cancellationToken);

        if (job is null)
        {
            return JobNotFound(id);
        }

        return TypedResults.Ok(JobStatusResponse.FromJob(job));
    }

    /// <summary>Download a job's compressed video</summary>
    /// <remarks>
    /// Available once the job's status is <c>Completed</c>, until the job is
    /// deleted. Supports range requests, so a download can be resumed or
    /// streamed.
    /// </remarks>
    /// <param name="id">The job's ID, as returned when it was submitted.</param>
    /// <response code="200">The compressed video, as an MP4.</response>
    /// <response code="206">The requested byte range of the compressed video.</response>
    /// <response code="404">There's no job with this ID, or it has been deleted.</response>
    /// <response code="409">The job hasn't completed: it's still queued or processing, or it failed.</response>
    internal static async Task<Results<PhysicalFileHttpResult, ProblemHttpResult>> GetJobResult(Guid id, DvcDbContext db, CancellationToken cancellationToken)
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

    /// <summary>Delete a job</summary>
    /// <remarks>
    /// Removes the job and its files. Deleting a queued job cancels it; a job
    /// that's being processed can't be deleted until it finishes. Jobs are
    /// never deleted automatically, so delete each one once you're done with
    /// it.
    /// </remarks>
    /// <param name="id">The job's ID, as returned when it was submitted.</param>
    /// <response code="204">The job and its files were deleted.</response>
    /// <response code="404">There's no job with this ID, or it has already been deleted.</response>
    /// <response code="409">The job is being processed. Try again once it has finished.</response>
    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteJob(
        Guid id,
        DvcDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // A queued job is cancelled by deleting its row, since the worker
        // skips IDs whose row is gone. A processing one is refused, since an
        // encode can't be stopped partway.
        //
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

using System.Globalization;
using Dvc.Api.Infrastructure.Data;
using Dvc.Api.Infrastructure.OpenApi;
using Dvc.Api.Models.Definitions;
using Dvc.Api.Models.DTOs.Requests;
using Dvc.Api.Models.DTOs.Responses;
using Dvc.Api.Models.Entities;
using Dvc.Api.Models.Options;
using Dvc.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Dvc.Api.Endpoints;

public static class JobEndpoints
{
    // The form fields SubmitJob accepts besides the file itself.
    private static readonly string[] FieldNames =
    [
        nameof(SubmitJobRequest.QualityPreference),
        nameof(SubmitJobRequest.Resolution),
        nameof(SubmitJobRequest.Fps),
        nameof(SubmitJobRequest.TargetSizeMb),
        nameof(SubmitJobRequest.EncoderPreset),
    ];

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

    // Parses the multipart body with MultipartReader rather than binding
    // [FromForm] IFormFile, which buffers the whole upload to a temp file
    // and then needs a second copy. Sections are checked in the order they
    // arrive, so a client that sends its fields before the file never
    // uploads a video that a bad field would reject.
    private static async Task<Results<AcceptedAtRoute<JobSubmittedResponse>, ProblemHttpResult>> SubmitJob(
        HttpRequest httpRequest,
        IJobSubmissionService jobSubmissionService,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Checked before reading any of the body, so a client waiting on
        // "Expect: 100-continue" never starts uploading.
        if (httpRequest.ContentLength > UploadDefinitions.MaxUploadBytes)
        {
            return UploadTooLarge();
        }

        if (!httpRequest.HasFormContentType ||
            !MediaTypeHeaderValue.TryParse(httpRequest.ContentType, out var mediaType))
        {
            return BadRequest("Expected multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return BadRequest("Missing multipart boundary.");
        }

        string? inputPath = null;
        var submitted = false;

        try
        {
            string? originalFileName = null;
            long fileSizeBytes = 0;
            double? targetSizeMb = null;
            string? resolution = null;
            double? fps = null;
            string? qualityPreference = null;
            string? encoderPreset = null;
            var receivedFields = new HashSet<string>();

            var reader = new MultipartReader(boundary, httpRequest.Body);
            for (var section = await reader.ReadNextSectionAsync(cancellationToken);
                 section is not null;
                 section = await reader.ReadNextSectionAsync(cancellationToken))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    continue;
                }

                if (HasFileContentDisposition(contentDisposition))
                {
                    if (inputPath is not null)
                    {
                        return BadRequest("Only one video file may be uploaded per job.");
                    }

                    originalFileName = contentDisposition.FileName.Value ?? contentDisposition.FileNameStar.Value;

                    if (string.IsNullOrEmpty(originalFileName))
                    {
                        return BadRequest("A video file is required.");
                    }

                    var extension = Path.GetExtension(originalFileName);
                    if (string.IsNullOrEmpty(extension) || !UploadDefinitions.AllowedExtensions.Contains(extension))
                    {
                        return BadRequest(
                            $"Unsupported file extension '{extension}'. Allowed: {string.Join(", ", UploadDefinitions.AllowedExtensions)}");
                    }

                    var storageRoot = configuration["Storage:InputDirectory"] ?? "Storage/Input";
                    Directory.CreateDirectory(storageRoot);
                    inputPath = Path.Combine(storageRoot, $"{Guid.NewGuid()}{extension}");

                    await using (var fileStream = File.Create(inputPath))
                    {
                        await section.Body.CopyToAsync(fileStream, cancellationToken);
                        fileSizeBytes = fileStream.Length;
                    }
                }
                else if (HasFormDataContentDisposition(contentDisposition))
                {
                    // Matched ignoring case. Unknown and repeated fields are
                    // rejected rather than ignored or overwritten, so a
                    // malformed request can't silently change the job. Checked
                    // before the value, so a blank field is caught too.
                    var sentName = contentDisposition.Name.Value;
                    var fieldName = FieldNames.FirstOrDefault(name =>
                        string.Equals(name, sentName, StringComparison.OrdinalIgnoreCase));
                    if (fieldName is null)
                    {
                        return BadRequest(
                            $"Unknown field '{sentName}'. Accepted: {string.Join(", ", FieldNames)}");
                    }

                    if (!receivedFields.Add(fieldName))
                    {
                        return BadRequest($"Field '{fieldName}' was sent more than once.");
                    }

                    using var streamReader = new StreamReader(section.Body);
                    var value = (await streamReader.ReadToEndAsync(cancellationToken)).Trim();

                    // Forms send a blank input as an empty value: not set.
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    // Rejects only values that could never encode; the rest
                    // pass through as given. Table lookups ignore case but
                    // store the table's spelling, since values go to ffmpeg
                    // and are echoed back in job status.
                    switch (fieldName)
                    {
                        case nameof(SubmitJobRequest.TargetSizeMb):
                            if (!TryParseNumber(value, out var parsedTargetSizeMb))
                                return ExpectedNumber(nameof(SubmitJobRequest.TargetSizeMb));
                            if (parsedTargetSizeMb <= 0)
                                return BadRequest("TargetSizeMb must be greater than zero.");
                            targetSizeMb = parsedTargetSizeMb;
                            break;
                        case nameof(SubmitJobRequest.Fps):
                            if (!TryParseNumber(value, out var parsedFps))
                                return ExpectedNumber(nameof(SubmitJobRequest.Fps));
                            if (parsedFps <= 0)
                                return BadRequest("Fps must be greater than zero.");
                            fps = parsedFps;
                            break;
                        case nameof(SubmitJobRequest.Resolution):
                            resolution = ResolutionDefinitions.ShortSides.CanonicalKey(value);
                            if (resolution is null)
                                return Unsupported("resolution", value, ResolutionDefinitions.ShortSides.Keys);
                            break;
                        case nameof(SubmitJobRequest.QualityPreference):
                            qualityPreference = QualityPreferenceDefinitions.Tiers.CanonicalKey(value);
                            if (qualityPreference is null)
                                return Unsupported("quality preference", value, QualityPreferenceDefinitions.Tiers.Keys);
                            break;
                        case nameof(SubmitJobRequest.EncoderPreset):
                            encoderPreset = EncoderPresetDefinitions.Presets.CanonicalKey(value);
                            if (encoderPreset is null)
                                return Unsupported("encoder preset", value, EncoderPresetDefinitions.Presets.Keys);
                            break;
                    }
                }
            }

            if (inputPath is null || fileSizeBytes == 0)
            {
                return BadRequest("A video file is required.");
            }

            var job = await jobSubmissionService.SubmitAsync(new SubmitJobRequest
            {
                InputPath = inputPath,
                OriginalFileName = originalFileName!,
                FileSizeBytes = fileSizeBytes,
                TargetSizeMb = targetSizeMb,
                Resolution = resolution,
                Fps = fps,
                QualityPreference = qualityPreference,
                EncoderPreset = encoderPreset
            }, cancellationToken);
            submitted = true;

            // By route name, so the Location header includes the group prefix.
            return TypedResults.AcceptedAtRoute(JobSubmittedResponse.FromJob(job), "GetJobStatus", new { id = job.Id });
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body only hits Kestrel's limit mid-stream.
            return UploadTooLarge();
        }
        finally
        {
            // Without a queued job, nothing else will consume the upload.
            if (!submitted && inputPath is not null)
            {
                File.Delete(inputPath);
            }
        }
    }

    // Every error is a problem details response (RFC 9457), with the message
    // in "detail".
    private static ProblemHttpResult Problem(int statusCode, string detail) =>
        TypedResults.Problem(detail, statusCode: statusCode);

    private static ProblemHttpResult BadRequest(string detail) =>
        Problem(StatusCodes.Status400BadRequest, detail);

    private static ProblemHttpResult JobNotFound(Guid id) =>
        Problem(StatusCodes.Status404NotFound, $"No job with ID {id}.");

    private static ProblemHttpResult UploadTooLarge() =>
        Problem(
            StatusCodes.Status413PayloadTooLarge,
            $"Upload exceeds the {UploadDefinitions.MaxUploadBytes / (1024 * 1024)}MB limit.");

    private static ProblemHttpResult ExpectedNumber(string field) =>
        BadRequest($"Expected a number for: {field}.");

    private static ProblemHttpResult Unsupported(string parameter, string value, IEnumerable<string> allowed) =>
        BadRequest($"Unsupported {parameter} '{value}'. Allowed: {string.Join(", ", allowed)}");

    // Invariant culture, so "29.97" parses regardless of server locale.
    // Rejects NaN/infinity, which ffmpeg can't act on.
    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    private static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition) =>
        contentDisposition.DispositionType.Equals("form-data") &&
        (!StringSegment.IsNullOrEmpty(contentDisposition.FileName) ||
         !StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar));

    private static bool HasFormDataContentDisposition(ContentDispositionHeaderValue contentDisposition) =>
        contentDisposition.DispositionType.Equals("form-data") &&
        StringSegment.IsNullOrEmpty(contentDisposition.FileName) &&
        StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar);

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

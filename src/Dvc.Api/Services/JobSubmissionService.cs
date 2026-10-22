using Dvc.Api.Infrastructure;
using Dvc.Api.Infrastructure.Data;
using Dvc.Api.Models.Definitions;
using Dvc.Api.Models.DTOs.Requests;
using Dvc.Api.Models.Entities;
using Dvc.Api.Models.Options;
using Microsoft.Extensions.Options;

namespace Dvc.Api.Services;

public class JobSubmissionService(
    DvcDbContext db,
    IJobQueue jobQueue,
    IOptions<EncodingOptions> encodingOptions,
    ILogger<JobSubmissionService> logger) : IJobSubmissionService
{
    public async Task<Job> SubmitAsync(SubmitJobRequest request, CancellationToken cancellationToken)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Queued,
            InputPath = request.InputPath,
            CreatedAt = DateTimeOffset.UtcNow,
            Resolution = request.Resolution,
            Fps = request.Fps,
            EncoderPreset = request.EncoderPreset ?? encodingOptions.Value.Preset
        };

        if (request.TargetSizeBytes is not null)
        {
            // A target size takes priority over a quality preference.
            job.TargetSizeBytes = request.TargetSizeBytes.Value;

            if (request.QualityPreference is not null)
            {
                job.Warning = "QualityPreference is ignored when TargetSizeBytes is set explicitly.";
            }
        }
        else
        {
            job.QualityPreference = request.QualityPreference ?? QualityPreferenceDefinitions.Default;
        }

        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // Compression runs out-of-band in JobProcessingWorker.
        jobQueue.Enqueue(job.Id);

        var target = job.TargetSizeBytes is { } targetSizeBytes
            ? $"target {targetSizeBytes} bytes"
            : $"preference {job.QualityPreference}";
        logger.LogInformation(
            "Queued job {JobId} ({FileName}, {FileSizeBytes} bytes, {Target})",
            job.Id, request.OriginalFileName, request.FileSizeBytes, target);

        return job;
    }
}

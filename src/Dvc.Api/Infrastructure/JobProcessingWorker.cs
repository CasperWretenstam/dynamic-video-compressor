using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Dvc.Api.Infrastructure.Data;
using Dvc.Api.Models.Entities;
using Dvc.Api.Models.Options;
using Dvc.Core.Compression;
using Dvc.Core.Definitions;

namespace Dvc.Api.Infrastructure;

/// <summary>
/// Compresses queued jobs one at a time, in the order they were submitted,
/// taking each job's ID from the job queue (<c>IJobQueue</c>).
/// </summary>
/// <remarks>
/// Each job gets its own dependency scope, since the database context is
/// scoped while this service lives for the whole app.
/// </remarks>
public class JobProcessingWorker : BackgroundService
{
    // Replaces any failure that isn't a CompressionFailedException. Those mostly
    // carry ffmpeg's raw output, which is logged but not meant for callers.
    private const string GenericFailureMessage =
        "The video could not be encoded. The source file may be corrupt or use an unsupported codec.";

    private readonly IJobQueue _jobQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptions<EncodingOptions> _encodingOptions;
    private readonly ILogger<JobProcessingWorker> _logger;

    public JobProcessingWorker(
        IJobQueue jobQueue,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptions<EncodingOptions> encodingOptions,
        ILogger<JobProcessingWorker> logger)
    {
        _jobQueue = jobQueue;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _encodingOptions = encodingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await foreach (Guid jobId in _jobQueue.DequeueAllAsync(cancellationToken))
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DvcDbContext>();
            var videoCompressionService = scope.ServiceProvider.GetRequiredService<IVideoCompressionService>();

            // Tags every line logged while processing, including the
            // compression engine's, which doesn't know about jobs.
            using var logScope = _logger.BeginScope("Job {JobId}", jobId);

            try
            {
                await ProcessJobAsync(jobId, dbContext, videoCompressionService, cancellationToken);
            }
            // Shutdown, not a job failure. The job stays marked as processing
            // with its input intact, so the next startup discards the partial
            // output and re-runs it.
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Job {JobId} interrupted by shutdown; it will be re-run on next startup", jobId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process job {JobId}", jobId);
            }
        }
    }

    private async Task ProcessJobAsync(
        Guid jobId,
        DvcDbContext dbContext,
        IVideoCompressionService videoCompressionService,
        CancellationToken cancellationToken)
    {
        // One conditional update, so claiming can't interleave with a delete:
        // a job deleted while queued is skipped, and a claimed one can't be
        // deleted. The output path is saved before encoding, so after a crash
        // the next startup knows which partial file to discard.
        var outputPath = BuildOutputPath(jobId);
        var claimed = await dbContext.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Processing)
                .SetProperty(j => j.OutputPath, outputPath), cancellationToken);

        if (claimed == 0)
        {
            _logger.LogInformation("Skipped job {JobId}: deleted while queued", jobId);
            return;
        }

        Job job = await dbContext.Jobs.FindAsync([jobId], cancellationToken)
            ?? throw new InvalidOperationException($"Job with ID {jobId} not found.");

        _logger.LogInformation("Started processing job {JobId}", jobId);

        try
        {
            var request = BuildCompressionRequest(job);
            var source = await videoCompressionService.ProbeAsync(request.InputPath, cancellationToken);

            // Saved before planning the encode, which can fail, so the caller
            // still sees what was attempted.
            var output = CompressionPlanner.PlanOutput(request, source);
            job.OutputWidth = output.Width;
            job.OutputHeight = output.Height;
            job.OutputFps = output.Fps;

            // Plan warnings are kept even if the encode then fails; the
            // encode's own only exist on success.
            var plan = CompressionPlanner.PlanEncode(request, source, output);
            AppendWarnings(job, plan.Warnings);

            var result = await videoCompressionService.EncodeAsync(plan, outputPath, cancellationToken);
            AppendWarnings(job, result.Warnings);

            job.Status = JobStatus.Completed;
            _logger.LogInformation("Completed job {JobId}", jobId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex is CompressionFailedException ? ex.Message : GenericFailureMessage;
            _logger.LogWarning(ex, "Job {JobId} failed during compression", jobId);

            // Whatever ffmpeg wrote before failing is partial or oversized.
            TryDeleteFile(outputPath, jobId);
            job.OutputPath = null;
        }

        // Not cancellable: a shutdown landing here shouldn't throw away a
        // finished encode.
        job.CompletedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        // The input isn't needed again, whatever the outcome. Deleted after
        // the status is saved, so a cleanup failure can't block it.
        TryDeleteFile(job.InputPath, jobId);
    }

    // The job's settings with the configured defaults filled in. The
    // resolution label becomes its short side in pixels.
    private CompressionRequest BuildCompressionRequest(Job job)
    {
        var options = _encodingOptions.Value;
        return new CompressionRequest
        {
            InputPath = job.InputPath,
            TargetSizeBytes = job.TargetSizeBytes,
            QualityPreference = job.QualityPreference,
            ShortSide = job.Resolution is null ? null : ResolutionDefinitions.ShortSides[job.Resolution],
            Fps = job.Fps,
            EncoderPreset = job.EncoderPreset,
            TwoPass = options.TwoPass,
            DefaultMaxShortSide = options.DefaultMaxShortSide,
            DefaultMaxFps = options.DefaultMaxFps,
        };
    }

    // Appended rather than overwritten, so a warning from submission is kept.
    private static void AppendWarnings(Job job, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            job.Warning = job.Warning is null ? warning : $"{job.Warning} {warning}";
        }
    }

    private string BuildOutputPath(Guid jobId)
    {
        var storageRoot = _configuration["Storage:OutputDirectory"] ?? "Storage/Output";
        Directory.CreateDirectory(storageRoot);

        // Absolute, as TypedResults.PhysicalFile requires when serving it.
        return Path.GetFullPath(Path.Combine(storageRoot, $"{jobId}.mp4"));
    }

    private void TryDeleteFile(string path, Guid jobId)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete file for job {JobId} at {Path}", jobId, path);
        }
    }
}

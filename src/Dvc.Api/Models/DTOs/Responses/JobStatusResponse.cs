using Dvc.Api.Models.Entities;

namespace Dvc.Api.Models.DTOs.Responses;

/// <summary>
/// A job's progress, the settings it was submitted with, and the output
/// those settings resolved to.
/// </summary>
/// <param name="Id">The job's ID.</param>
/// <param name="Status">Where the job is in processing.</param>
/// <param name="ErrorMessage">
/// Why the job failed, written so it can be shown to end users. Null unless
/// <c>status</c> is <c>Failed</c>.
/// </param>
/// <param name="Warning">
/// How the job differs from what was requested, without having stopped it:
/// for example a <c>QualityPreference</c> ignored because <c>TargetSizeMb</c>
/// was also set, or an output that comes in under its target size. Null when
/// there's nothing to report.
/// </param>
/// <param name="CreatedAt">When the job was submitted.</param>
/// <param name="CompletedAt">
/// When the job finished, whether it completed or failed. Null until then.
/// </param>
/// <param name="Settings">The settings the job was submitted with.</param>
/// <param name="Output">
/// The dimensions and framerate the video is encoded to. Null until
/// processing has analysed the video.
/// </param>
public sealed record JobStatusResponse(
    Guid Id,
    JobStatus Status,
    string? ErrorMessage,
    string? Warning,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    JobSettings Settings,
    JobOutput? Output)
{
    public static JobStatusResponse FromJob(Job job) => new(
        job.Id,
        job.Status,
        job.ErrorMessage,
        job.Warning,
        job.CreatedAt,
        job.CompletedAt,
        new JobSettings(job.QualityPreference, job.TargetSizeMb, job.Resolution, job.Fps, job.EncoderPreset),
        job.OutputWidth is { } width && job.OutputHeight is { } height
            ? new JobOutput(width, height, job.OutputFps)
            : null);
}

/// <summary>
/// The settings a job was submitted with, with this deployment's defaults
/// filled in where they don't depend on the video.
/// </summary>
/// <param name="QualityPreference">
/// The quality level the video is encoded at. Null when the job aims for a
/// file size (<c>targetSizeMb</c>) instead.
/// </param>
/// <param name="TargetSizeMb">
/// The file size, in MB, the output stays under. Null when the job encodes
/// at a quality level (<c>qualityPreference</c>) instead.
/// </param>
/// <param name="Resolution">
/// The requested resolution. Null when it follows the video's own; the
/// job's <c>output</c> shows the dimensions it resolved to.
/// </param>
/// <param name="Fps">
/// The requested framerate. Null when it follows the video's own; the job's
/// <c>output</c> shows the framerate it resolved to.
/// </param>
/// <param name="EncoderPreset">
/// The encoder preset: the one submitted, or this deployment's default.
/// </param>
public sealed record JobSettings(
    string? QualityPreference,
    double? TargetSizeMb,
    string? Resolution,
    double? Fps,
    string EncoderPreset);

/// <summary>The dimensions and framerate a job's video is encoded to.</summary>
/// <param name="Width">The output width, in pixels.</param>
/// <param name="Height">The output height, in pixels.</param>
/// <param name="Fps">
/// The output framerate: the requested one, or else the video's own, lowered
/// to this deployment's limit if it's higher. Null when none was requested
/// and the video's own couldn't be determined.
/// </param>
/// <remarks>
/// Set once processing has probed the source, so a job that failed after
/// that still reports it.
/// </remarks>
public sealed record JobOutput(int Width, int Height, double? Fps);

using Dvc.Api.Models.Entities;

namespace Dvc.Api.Models.DTOs.Responses;

/// <summary>The body of <c>GET /v1/jobs/{id}</c>.</summary>
/// <param name="Settings">What the job was submitted with.</param>
/// <param name="Output">
/// The output dimensions and framerate the submitted settings
/// (<see cref="Settings"/>) resolved to. Null until processing has probed
/// the source.
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

/// <summary>The settings a job was submitted with.</summary>
/// <param name="Resolution">
/// Null when it follows the source. The resulting output dimensions are
/// reported separately (<see cref="JobOutput"/>).
/// </param>
/// <param name="Fps">
/// Null when it follows the source. The resulting output framerate is
/// reported separately (<see cref="JobOutput"/>).
/// </param>
public sealed record JobSettings(
    string? QualityPreference,
    double? TargetSizeMb,
    string? Resolution,
    double? Fps,
    string EncoderPreset);

/// <summary>
/// The output dimensions and framerate a job's settings resolved to for its
/// source.
/// </summary>
/// <param name="Fps">The source's own rate when it was kept as-is; null if it couldn't be determined.</param>
public sealed record JobOutput(int Width, int Height, double? Fps);

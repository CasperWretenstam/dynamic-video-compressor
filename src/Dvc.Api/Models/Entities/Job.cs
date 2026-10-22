namespace Dvc.Api.Models.Entities;

/// <summary>A compression job, as stored in the database.</summary>
public class Job
{
    public Guid Id { get; set; }
    public JobStatus Status { get; set; }

    /// <summary>
    /// Where the uploaded video is saved. The file is deleted once the job
    /// finishes, whatever the outcome.
    /// </summary>
    public required string InputPath { get; set; }

    /// <summary>
    /// Where the compressed video is written. Set when processing starts, so
    /// a partial file can be found and deleted after a crash; cleared if the
    /// job fails.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Why the job failed, written to be shown to end users. Null unless it
    /// failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// A non-fatal issue: the job didn't run exactly as requested, e.g. a
    /// quality preference ignored because a target size was also set.
    /// </summary>
    public string? Warning { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the job completed or failed. Null until then.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The file size to stay under, in bytes. Exactly one of this and the
    /// quality tier (<c>QualityPreference</c>) is set.
    /// </summary>
    public long? TargetSizeBytes { get; set; }

    /// <summary>
    /// The quality tier to encode at, one of the accepted tiers
    /// (<c>QualityPreferenceDefinitions.Tiers</c>). Exactly
    /// one of this and the target size (<c>TargetSizeBytes</c>) is set.
    /// </summary>
    public string? QualityPreference { get; set; }

    /// <summary>
    /// The requested resolution label, one of the accepted labels
    /// (<c>ResolutionDefinitions.ShortSides</c>). Null
    /// follows the source, lowered to the configured cap
    /// (<c>EncodingOptions.DefaultMaxShortSide</c>) if it's
    /// larger.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// The requested output framerate. Null follows the source, lowered to
    /// the configured cap (<c>EncodingOptions.DefaultMaxFps</c>)
    /// if it's higher.
    /// </summary>
    public double? Fps { get; set; }

    /// <summary>
    /// The libx264 preset, one of the accepted presets
    /// (<c>EncoderPresetDefinitions.Presets</c>).
    /// A job submitted without one gets the configured default
    /// (<c>EncodingOptions.Preset</c>) at submission, so a
    /// later config change doesn't affect it.
    /// </summary>
    public required string EncoderPreset { get; set; }

    /// <summary>
    /// The output width in pixels, set once the source has been probed.
    /// </summary>
    public int? OutputWidth { get; set; }

    /// <summary>
    /// The output height in pixels, set once the source has been probed.
    /// </summary>
    public int? OutputHeight { get; set; }

    /// <summary>
    /// The output framerate, set once the source has been probed: the
    /// requested or capped rate, or the source's average rate if kept as-is.
    /// Null if neither the job nor ffprobe supplied one.
    /// </summary>
    public double? OutputFps { get; set; }
}

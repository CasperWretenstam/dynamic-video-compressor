namespace Dvc.Api.Models.Entities;

public class Job
{
    public Guid Id { get; set; }
    public JobStatus Status { get; set; }
    public required string InputPath { get; set; }
    public string? OutputPath { get; set; }

    /// <summary>
    /// Why the job failed, written to be shown to end users. Null unless it
    /// failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// A non-fatal issue with a job that still completed, e.g. a quality
    /// preference ignored because a target size was also set.
    /// </summary>
    public string? Warning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The file size to stay under, in MB. Exactly one of this and the
    /// quality tier (<see cref="QualityPreference"/>) is set.
    /// </summary>
    public double? TargetSizeMb { get; set; }

    /// <summary>
    /// The quality tier to encode at, one of the accepted tiers
    /// (<see cref="Definitions.QualityPreferenceDefinitions.Tiers"/>). Exactly
    /// one of this and the target size (<see cref="TargetSizeMb"/>) is set.
    /// </summary>
    public string? QualityPreference { get; set; }

    /// <summary>
    /// The requested resolution label, one of the accepted labels
    /// (<see cref="Definitions.ResolutionDefinitions.ShortSides"/>). Null
    /// follows the source, lowered to the configured cap
    /// (<see cref="Options.EncodingOptions.DefaultMaxShortSide"/>) if it's
    /// larger.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// The requested output framerate. Null follows the source, lowered to
    /// the configured cap (<see cref="Options.EncodingOptions.DefaultMaxFps"/>)
    /// if it's higher.
    /// </summary>
    public double? Fps { get; set; }

    /// <summary>
    /// The libx264 preset, one of the accepted presets
    /// (<see cref="Definitions.EncoderPresetDefinitions.Presets"/>).
    /// A job submitted without one gets the configured default
    /// (<see cref="Options.EncodingOptions.Preset"/>) at submission, so a
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

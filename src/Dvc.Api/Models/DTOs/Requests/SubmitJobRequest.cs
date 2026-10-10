namespace Dvc.Api.Models.DTOs.Requests;

/// <summary>
/// A <c>POST /v1/jobs</c> submission whose fields have all passed
/// validation. The uploaded file is already saved at its final location by
/// the time this is built.
/// </summary>
public class SubmitJobRequest
{
    public required string InputPath { get; set; }
    public required string OriginalFileName { get; set; }
    public required long FileSizeBytes { get; set; }

    /// <summary>
    /// Compress to at most this many MB, e.g. to fit an upload limit. Takes
    /// priority over a quality tier (<see cref="QualityPreference"/>) when
    /// both are set.
    /// </summary>
    public double? TargetSizeMb { get; set; }

    /// <summary>
    /// One of the accepted resolution labels
    /// (<see cref="Definitions.ResolutionDefinitions.ShortSides"/>), or null
    /// to follow the source.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>The output framerate, or null to follow the source.</summary>
    public double? Fps { get; set; }

    /// <summary>
    /// One of the accepted quality tiers
    /// (<see cref="Definitions.QualityPreferenceDefinitions.Tiers"/>), letting
    /// file size follow the content. Ignored when a target size
    /// (<see cref="TargetSizeMb"/>) is set; when neither is, the default tier
    /// (<see cref="Definitions.QualityPreferenceDefinitions.Default"/>) applies.
    /// </summary>
    public string? QualityPreference { get; set; }

    /// <summary>
    /// One of the accepted libx264 presets
    /// (<see cref="Definitions.EncoderPresetDefinitions.Presets"/>), or null for
    /// the configured default (<see cref="Options.EncodingOptions.Preset"/>).
    /// </summary>
    public string? EncoderPreset { get; set; }
}

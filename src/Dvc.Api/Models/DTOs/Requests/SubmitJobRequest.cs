namespace Dvc.Api.Models.DTOs.Requests;

/// <summary>
/// A <c>POST /v1/jobs</c> submission whose fields have all passed
/// validation. The uploaded file is already saved at its final location by
/// the time this is built.
/// </summary>
public class SubmitJobRequest
{
    /// <summary>Where the uploaded video was saved.</summary>
    public required string InputPath { get; set; }

    /// <summary>The upload's file name as the client sent it. Only logged.</summary>
    public required string OriginalFileName { get; set; }

    public required long FileSizeBytes { get; set; }

    /// <summary>
    /// Compress to at most this many bytes, e.g. to fit an upload limit.
    /// Takes priority over a quality tier (<c>QualityPreference</c>) when
    /// both are set.
    /// </summary>
    public long? TargetSizeBytes { get; set; }

    /// <summary>
    /// One of the accepted resolution labels
    /// (<c>ResolutionDefinitions.ShortSides</c>), or null
    /// to follow the source.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>The output framerate, or null to follow the source.</summary>
    public double? Fps { get; set; }

    /// <summary>
    /// One of the accepted quality tiers
    /// (<c>QualityPreferenceDefinitions.Tiers</c>), letting
    /// file size follow the content. Ignored when a target size
    /// (<c>TargetSizeBytes</c>) is set; when neither is, the default tier
    /// (<c>QualityPreferenceDefinitions.Default</c>) applies.
    /// </summary>
    public string? QualityPreference { get; set; }

    /// <summary>
    /// One of the accepted libx264 presets
    /// (<c>EncoderPresetDefinitions.Presets</c>), or null for
    /// the configured default (<c>EncodingOptions.Preset</c>).
    /// </summary>
    public string? EncoderPreset { get; set; }
}

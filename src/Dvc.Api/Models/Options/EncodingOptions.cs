using Dvc.Api.Models.Definitions;

namespace Dvc.Api.Models.Options;

/// <summary>
/// Operator-level encoding settings, bound from the "Encoding" config
/// section and validated at startup.
/// </summary>
public class EncodingOptions
{
    public const string SectionName = "Encoding";

    /// <summary>
    /// Whether jobs aiming for a file size
    /// (<c>SubmitJobRequest.TargetSizeBytes</c>) encode in
    /// two passes: slower, but the encoder sees the whole clip's complexity
    /// up front. Quality-tier jobs always use a single pass.
    /// </summary>
    public bool TwoPass { get; set; } = true;

    /// <summary>
    /// The libx264 preset used when a job doesn't set its own; one of the
    /// accepted presets (<c>EncoderPresetDefinitions.Presets</c>).
    /// </summary>
    public string Preset { get; set; } = EncoderPresetDefinitions.Default;

    /// <summary>
    /// Caps the output's short side, in pixels, when a job doesn't request a
    /// resolution (<c>SubmitJobRequest.Resolution</c>).
    /// Never upscales. Null (empty in config) disables the cap.
    /// </summary>
    public int? DefaultMaxShortSide { get; set; }

    /// <summary>
    /// Caps the output framerate when a job doesn't request one
    /// (<c>SubmitJobRequest.Fps</c>). Never raises it.
    /// Null (empty in config) disables the cap.
    /// </summary>
    public double? DefaultMaxFps { get; set; }
}

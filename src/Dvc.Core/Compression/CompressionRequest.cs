namespace Dvc.Core.Compression;

/// <summary>
/// What to compress and how, with every setting already resolved: the engine
/// reads no configuration of its own. Exactly one of a target size
/// (<c>TargetSizeBytes</c>) and a quality tier (<c>QualityPreference</c>)
/// is set.
/// </summary>
public sealed record CompressionRequest
{
    /// <summary>The video file to compress.</summary>
    public required string InputPath { get; init; }

    /// <summary>
    /// The file size to stay under, in bytes, encoded at a bitrate derived
    /// from it. Null when a quality tier (<c>QualityPreference</c>) is set
    /// instead.
    /// </summary>
    public long? TargetSizeBytes { get; init; }

    /// <summary>
    /// The quality tier to encode at, one of the accepted tiers
    /// (<c>QualityPreferenceDefinitions.Tiers</c>). Null when a target size
    /// (<c>TargetSizeBytes</c>) is set instead.
    /// </summary>
    public string? QualityPreference { get; init; }

    /// <summary>
    /// The output's short side in pixels, used as given, including
    /// upscaling. Null follows the source, lowered to the default cap
    /// (<c>DefaultMaxShortSide</c>) if it's larger.
    /// </summary>
    public int? ShortSide { get; init; }

    /// <summary>
    /// The output framerate, used as given, including raising it. Null
    /// follows the source, lowered to the default cap (<c>DefaultMaxFps</c>)
    /// if it's higher.
    /// </summary>
    public double? Fps { get; init; }

    /// <summary>
    /// The libx264 preset, one of the accepted presets
    /// (<c>EncoderPresetDefinitions.Presets</c>).
    /// </summary>
    public required string EncoderPreset { get; init; }

    /// <summary>
    /// Whether an encode aiming for a file size (<c>TargetSizeBytes</c>)
    /// runs in two passes: slower, but the encoder sees the whole clip's
    /// complexity up front. A quality-tier encode always runs one pass.
    /// </summary>
    public required bool TwoPass { get; init; }

    /// <summary>
    /// Caps the output's short side, in pixels, when no short side
    /// (<c>ShortSide</c>) is requested. Never upscales. Null disables the
    /// cap.
    /// </summary>
    public int? DefaultMaxShortSide { get; init; }

    /// <summary>
    /// Caps the output framerate when no framerate (<c>Fps</c>) is
    /// requested. Never raises it. Null disables the cap.
    /// </summary>
    public double? DefaultMaxFps { get; init; }
}

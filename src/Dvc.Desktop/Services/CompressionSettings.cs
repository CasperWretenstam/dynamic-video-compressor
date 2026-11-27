using System.Text.Json.Serialization;
using Dvc.Core.Compression;
using Dvc.Core.Definitions;

namespace Dvc.Desktop.Services;

/// <summary>
/// The compression settings the user chose, applied to every video in the
/// queue as it starts. The defaults match the HTTP service's default
/// settings: the Balanced quality tier, the source's resolution and
/// framerate capped at a 1080-pixel short side and 30fps, and the slow
/// encoder preset.
/// </summary>
public sealed record CompressionSettings
{
    /// <summary>The settings a first run uses, and what Reset to defaults restores.</summary>
    public static CompressionSettings Default { get; } = new();

    /// <summary>The short sides, in pixels, the resolution can be capped at, largest first.</summary>
    public static IReadOnlyList<int> ShortSideCaps { get; } =
        [.. ResolutionDefinitions.ShortSides.Values.OrderDescending()];

    /// <summary>The framerates the framerate can be capped at, highest first.</summary>
    public static IReadOnlyList<double> FpsCaps { get; } = [60, 30, 24];

    /// <summary>The smallest file size, in MB, an encode can aim for.</summary>
    public const decimal MinTargetSizeMb = 0.1m;

    /// <summary>The largest file size, in MB, an encode can aim for.</summary>
    public const decimal MaxTargetSizeMb = 1_000_000m;

    /// <summary>
    /// Bytes in a megabyte, as 1024 × 1024, matching how the app and Windows
    /// Explorer show file sizes.
    /// </summary>
    public const long BytesPerMb = 1024 * 1024;

    /// <summary>
    /// The quality tier to encode at, one of the accepted tiers
    /// (<c>QualityPreferenceDefinitions.Tiers</c>). Not used while aiming for
    /// a file size (<c>AimForFileSize</c>).
    /// </summary>
    public string Quality { get; init; } = QualityPreferenceDefinitions.Default;

    /// <summary>
    /// The largest output short side, in pixels, one of the caps on offer
    /// (<c>ShortSideCaps</c>). A smaller source keeps its own, so nothing is
    /// upscaled. Null keeps the source's resolution whatever it is.
    /// </summary>
    public int? MaxShortSide { get; init; } = 1080;

    /// <summary>
    /// The highest output framerate, one of the caps on offer
    /// (<c>FpsCaps</c>). A slower source keeps its own, so the framerate is
    /// never raised. Null keeps the source's framerate whatever it is.
    /// </summary>
    public double? MaxFps { get; init; } = 30;

    /// <summary>
    /// The libx264 preset, one of the accepted presets
    /// (<c>EncoderPresetDefinitions.Presets</c>).
    /// </summary>
    public string EncoderPreset { get; init; } = EncoderPresetDefinitions.Default;

    /// <summary>
    /// Whether to aim for a file size (<c>TargetSizeMb</c>) instead of
    /// encoding at a quality tier (<c>Quality</c>).
    /// </summary>
    public bool AimForFileSize { get; init; }

    /// <summary>
    /// The file size to aim for, in MB of 1024 × 1024 bytes, when aiming for
    /// one (<c>AimForFileSize</c>). Kept while not aiming for one, so turning
    /// it back on brings back the last size.
    /// </summary>
    public decimal TargetSizeMb { get; init; } = 25;

    /// <summary>The file size to aim for (<c>TargetSizeMb</c>), in bytes.</summary>
    [JsonIgnore]
    public long TargetSizeBytes => (long)Math.Round(TargetSizeMb * BytesPerMb);

    /// <summary>The compression request for the video at <c>inputPath</c>, with these settings filled in.</summary>
    public CompressionRequest CreateRequest(string inputPath) => new()
    {
        InputPath = inputPath,
        QualityPreference = AimForFileSize ? null : Quality,
        TargetSizeBytes = AimForFileSize ? TargetSizeBytes : null,
        EncoderPreset = EncoderPreset,
        // Two passes land closer to the size; it only affects encodes aiming
        // for a file size.
        TwoPass = true,
        DefaultMaxShortSide = MaxShortSide,
        DefaultMaxFps = MaxFps,
    };

    /// <summary>Whether <c>value</c> is a resolution cap on offer, or null for the source's own.</summary>
    public static bool IsValidMaxShortSide(int? value) => value is null || ShortSideCaps.Contains(value.Value);

    /// <summary>Whether <c>value</c> is a framerate cap on offer, or null for the source's own.</summary>
    public static bool IsValidMaxFps(double? value) => value is null || FpsCaps.Contains(value.Value);

    /// <summary>Whether <c>value</c> is a file size, in MB, an encode can aim for.</summary>
    public static bool IsValidTargetSizeMb(decimal value) => value is >= MinTargetSizeMb and <= MaxTargetSizeMb;
}

using Dvc.Core.Compression;
using Dvc.Core.Definitions;

namespace Dvc.Desktop.Services;

/// <summary>
/// The settings every compression in the app uses. They match the HTTP
/// service's default settings: the Balanced quality tier, the
/// source's resolution and framerate capped at a 1080-pixel short side and
/// 30fps, and the slow encoder preset.
/// </summary>
public static class FixedCompressionSettings
{
    /// <summary>The largest output short side, in pixels. A smaller source keeps its own.</summary>
    public const int MaxShortSide = 1080;

    /// <summary>The highest output framerate. A slower source keeps its own.</summary>
    public const double MaxFps = 30;

    /// <summary>The compression request for the video at <c>inputPath</c>, with these settings filled in.</summary>
    public static CompressionRequest CreateRequest(string inputPath) => new()
    {
        InputPath = inputPath,
        QualityPreference = QualityPreferenceDefinitions.Balanced,
        EncoderPreset = EncoderPresetDefinitions.Default,
        // Only affects encodes aiming for a file size, which the app doesn't
        // make; set to the engine's usual choice.
        TwoPass = true,
        DefaultMaxShortSide = MaxShortSide,
        DefaultMaxFps = MaxFps,
    };
}

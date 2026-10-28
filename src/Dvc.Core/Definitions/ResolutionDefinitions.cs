namespace Dvc.Core.Definitions;

/// <summary>
/// The resolution labels the engine accepts. A label sets the output's short
/// side and the long side follows the source's aspect ratio, so "1080p" is
/// 1920x1080 for landscape 16:9 and 1080x1920 for portrait.
/// </summary>
/// <remarks>
/// The short side keeps a label's meaning consistent for any orientation or
/// aspect ratio, so one can be picked without knowing the source's shape.
/// </remarks>
public static class ResolutionDefinitions
{
    /// <summary>Each accepted label and the short side, in pixels, it sets.</summary>
    public static readonly IReadOnlyDictionary<string, int> ShortSides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["360p"] = 360,
        ["480p"] = 480,
        ["720p"] = 720,
        ["1080p"] = 1080,
        ["1440p"] = 1440,
        ["4K"] = 2160,
    };
}

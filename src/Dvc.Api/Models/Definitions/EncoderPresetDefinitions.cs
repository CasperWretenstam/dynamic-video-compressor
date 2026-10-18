namespace Dvc.Api.Models.Definitions;

/// <summary>
/// The libx264 presets the API accepts. A preset sets how hard the encoder
/// works, not the quality it aims for: a slower one gives a smaller file at
/// a quality level (<c>SubmitJobRequest.QualityPreference</c>),
/// or better quality at a file size
/// (<c>SubmitJobRequest.TargetSizeMb</c>), at the cost
/// of encode time.
/// </summary>
public static class EncoderPresetDefinitions
{
    /// <summary>
    /// The preset used when none is configured
    /// (<c>EncodingOptions.Preset</c>). Its extra encode time
    /// is paid once; the storage savings last as long as the file.
    /// </summary>
    public const string Default = "slow";

    /// <summary>
    /// Each accepted preset, fastest first, with a description of its
    /// speed/size tradeoff for clients.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Presets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ultrafast"] = "Fastest encode, by far the largest files. For previews or throwaway output.",
        ["superfast"] = "Very fast encode, much larger files than medium.",
        ["veryfast"] = "Fast encode with a large size penalty; common for live streaming.",
        ["faster"] = "Quicker than medium, with somewhat larger files.",
        ["fast"] = "Slightly quicker than medium, with slightly larger files.",
        ["medium"] = "libx264's own default; a middle ground between speed and size.",
        ["slow"] = "Roughly 1.5-2x medium's encode time for files several percent smaller. Usually the best tradeoff.",
        ["slower"] = "Roughly 2.5-3x medium's encode time for a few percent smaller again.",
        ["veryslow"] = "Roughly 4x medium's encode time or more, for small further gains.",
        ["placebo"] = "Many times slower than veryslow for almost no gain. Rarely worth it.",
    };
}

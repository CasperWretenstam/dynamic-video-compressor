namespace Dvc.Api.Models.Definitions;

/// <summary>
/// The quality tiers the API accepts
/// (<c>SubmitJobRequest.QualityPreference</c>) and the
/// libx264 CRF (constant rate factor) each encodes at. CRF holds a consistent
/// perceived quality and lets the bitrate follow the content. Not used when
/// a job aims for a file size
/// (<c>SubmitJobRequest.TargetSizeBytes</c>).
/// </summary>
public static class QualityPreferenceDefinitions
{
    public const string SmallerSize = "SmallerSize";
    public const string Balanced = "Balanced";
    public const string HigherQuality = "HigherQuality";

    /// <summary>
    /// The tier used when a job sets neither a quality preference nor a
    /// target size.
    /// </summary>
    public const string Default = Balanced;

    public sealed record Tier(int Crf, string Description);

    /// <summary>
    /// Each accepted tier with its CRF at 1080p, adjusted for other output
    /// sizes on resolving (<c>ResolveCrf</c>), and a description for
    /// clients. Lower CRF is higher quality; each +6 roughly halves the bitrate.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Tier> Tiers = new Dictionary<string, Tier>(StringComparer.OrdinalIgnoreCase)
    {
        [SmallerSize] = new(26, "Smallest files. Fine detail and grain soften noticeably; suited to archives where space matters most."),
        [Balanced] = new(23, "Visually close to the source at normal viewing. The recommended default."),
        [HigherQuality] = new(20, "Hard to tell apart from the source, at the cost of noticeably larger files."),
    };

    private const double ReferencePixels = 1920 * 1080;
    private const int MinCrfOffset = -1;
    private const int MaxCrfOffset = 2;

    /// <summary>
    /// Resolves a quality-preference tier to the libx264 CRF it encodes at
    /// for the given output dimensions.
    /// </summary>
    /// <remarks>
    /// The same CRF looks cleaner at higher resolutions, where artifacts are
    /// smaller on screen, so it shifts by one per doubling of pixel count
    /// relative to 1080p (bounded to -1..+2). No fps adjustment: CRF already
    /// accounts for framerate.
    /// </remarks>
    public static int ResolveCrf(string qualityPreference, int width, int height)
    {
        if (!Tiers.TryGetValue(qualityPreference, out var tier))
        {
            throw new ArgumentException($"Unknown quality preference '{qualityPreference}'.", nameof(qualityPreference));
        }

        var doublings = Math.Log2(width * (double)height / ReferencePixels);
        var offset = Math.Clamp((int)Math.Round(doublings, MidpointRounding.AwayFromZero), MinCrfOffset, MaxCrfOffset);

        return tier.Crf + offset;
    }
}

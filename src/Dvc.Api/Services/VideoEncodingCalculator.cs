namespace Dvc.Api.Services;

/// <summary>
/// Bitrate, dimension and framerate math for encoding, as pure functions.
/// Works on pixel dimensions rather than resolution labels, so it applies to
/// any aspect ratio or output size.
/// </summary>
public static class VideoEncodingCalculator
{
    private const double BufferCoefficient = 0.9;

    // Reference point for the bitrate ceiling: 12Mbps at 1080p30, scaled
    // from there by pixel count and framerate.
    private const double ReferencePixels = 1920 * 1080;
    private const double ReferenceFps = 30;
    private const double ReferenceMaxVideoBitrateBps = 12_000_000;

    // Bitrate needs grow sub-linearly with pixel count and framerate: larger
    // frames carry more redundancy, and frames are more alike at higher fps.
    // 1080p -> 4K scales the ceiling ~3x rather than 4x; 30 -> 60fps ~1.5x.
    private const double PixelScalingExponent = 0.8;
    private const double FpsScalingExponent = 0.6;

    /// <summary>The AAC bitrate (bps) for a source whose audio bitrate ffprobe can't report.</summary>
    public const long DefaultAudioBitrateBps = 128_000;

    /// <summary>The highest AAC bitrate (bps) a source's own audio bitrate is carried over at.</summary>
    public const long MaxAudioBitrateBps = 192_000;

    /// <summary>
    /// Computes the video bitrate (bps) that hits a target file size, after
    /// reserving room for audio and applying a safety buffer. Cap the result
    /// at the source's own bitrate (<c>CapAtSourceBitrate</c>) before
    /// encoding.
    /// </summary>
    /// <param name="targetSizeMb">Desired output file size, in MB.</param>
    /// <param name="durationSeconds">Duration of the source video, in seconds.</param>
    /// <param name="audioBitrateBps">Audio bitrate to reserve, in bps.</param>
    public static long BitrateFromTargetSize(double targetSizeMb, double durationSeconds, long audioBitrateBps)
    {
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be greater than zero.");

        var targetSizeBytes = targetSizeMb * 1024 * 1024;
        var targetBitrate = targetSizeBytes * 8 / durationSeconds;
        var videoBitrate = Math.Max(0, targetBitrate - audioBitrateBps) * BufferCoefficient;

        return (long)videoBitrate;
    }

    /// <summary>
    /// The AAC bitrate (bps) to encode audio at: the source's own, capped at
    /// a ceiling (<c>MaxAudioBitrateBps</c>), or a fallback rate
    /// (<c>DefaultAudioBitrateBps</c>) when ffprobe can't report it.
    /// Zero when the source has no audio. The cap keeps a lossless source
    /// (~1.5Mbps) from carrying its rate over to AAC, which is transparent
    /// well below that, and eating a target size's video budget.
    /// </summary>
    public static long ResolveAudioBitrateBps(bool hasAudioStream, long? sourceAudioBitrateBps)
    {
        if (!hasAudioStream)
        {
            return 0;
        }

        return sourceAudioBitrateBps is > 0 ? Math.Min(sourceAudioBitrateBps.Value, MaxAudioBitrateBps) : DefaultAudioBitrateBps;
    }

    /// <summary>
    /// Caps a requested video bitrate at the source's own, when known:
    /// encoding above it only inflates the file without adding detail.
    /// </summary>
    /// <param name="requestedBitrateBps">The requested video bitrate, in bps.</param>
    /// <param name="sourceBitrateBps">
    /// The source video's own bitrate, in bps, or null if unknown, which
    /// leaves the request untouched.
    /// </param>
    /// <returns>
    /// The bitrate to encode at, and whether the cap lowered it, so the
    /// caller can surface a warning.
    /// </returns>
    public static (long BitrateBps, bool Capped) CapAtSourceBitrate(long requestedBitrateBps, long? sourceBitrateBps) =>
        sourceBitrateBps is > 0 && requestedBitrateBps > sourceBitrateBps
            ? (sourceBitrateBps.Value, true)
            : (requestedBitrateBps, false);

    /// <summary>
    /// The VBV maxrate (bps) for a constant-quality (CRF) encode: the
    /// output's bitrate ceiling, capped at the source's own bitrate when
    /// known. Bounds worst-case output size on unusually complex content
    /// without steering the encoder on typical content.
    /// </summary>
    /// <param name="width">Output width, in pixels.</param>
    /// <param name="height">Output height, in pixels.</param>
    /// <param name="fps">Output framerate.</param>
    /// <param name="sourceBitrateBps">
    /// The source video's own bitrate, in bps, or null if unknown.
    /// </param>
    public static long CrfMaxRateBps(int width, int height, double fps, long? sourceBitrateBps)
    {
        var maxRateBps = MaxVideoBitrateBps(width, height, fps);
        return sourceBitrateBps is > 0 ? Math.Min(maxRateBps, sourceBitrateBps.Value) : maxRateBps;
    }

    /// <summary>
    /// The video bitrate ceiling (bps) for the given output dimensions and
    /// framerate. Generous on purpose: a CRF encode only reaches it on
    /// unusually complex content, such as heavy grain or fast handheld motion.
    /// </summary>
    public static long MaxVideoBitrateBps(int width, int height, double fps)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be greater than zero.");
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "Fps must be greater than zero.");

        var pixelScale = Math.Pow(width * (double)height / ReferencePixels, PixelScalingExponent);
        var fpsScale = Math.Pow(fps / ReferenceFps, FpsScalingExponent);

        return (long)(ReferenceMaxVideoBitrateBps * pixelScale * fpsScale);
    }

    /// <summary>
    /// The source's dimensions as displayed. Phones often store portrait video
    /// as landscape frames with a 90/270-degree rotation tag, which ffmpeg
    /// applies on decode.
    /// </summary>
    public static (int Width, int Height) DisplayDimensions(int storedWidth, int storedHeight, int rotationDegrees) =>
        Math.Abs(rotationDegrees) % 180 == 90 ? (storedHeight, storedWidth) : (storedWidth, storedHeight);

    /// <summary>
    /// The output short side, in pixels: the requested one when set
    /// (honored as-is, including upscaling), otherwise the source's own,
    /// lowered to <c>maxShortSide</c> when that's set and smaller.
    /// </summary>
    public static int ResolveShortSide(int? requestedShortSide, int sourceShortSide, int? maxShortSide)
    {
        if (requestedShortSide is { } requested)
        {
            return requested;
        }

        return maxShortSide is { } max ? Math.Min(sourceShortSide, max) : sourceShortSide;
    }

    /// <summary>
    /// Computes output width/height that give the requested short side while
    /// preserving the source's aspect ratio and orientation.
    /// </summary>
    /// <remarks>
    /// Both dimensions are rounded to the nearest even number, since libx264
    /// with yuv420p rejects odd dimensions (e.g. 480p 16:9 is 853.33px wide,
    /// which becomes 854).
    /// </remarks>
    public static (int Width, int Height) CalculateOutputDimensions(int sourceWidth, int sourceHeight, int targetShortSide)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be greater than zero.");
        if (targetShortSide <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetShortSide), "Short side must be greater than zero.");

        var sourceShortSide = Math.Min(sourceWidth, sourceHeight);
        var sourceLongSide = Math.Max(sourceWidth, sourceHeight);

        var shortSide = RoundToEven(targetShortSide);
        var longSide = RoundToEven(sourceLongSide * (double)targetShortSide / sourceShortSide);

        return sourceWidth >= sourceHeight ? (longSide, shortSide) : (shortSide, longSide);
    }

    /// <summary>
    /// The output framerate to force, or null to keep the source's own
    /// timing untouched: the requested framerate when set (honored as-is,
    /// including raising it), otherwise <c>maxFps</c> when the
    /// source is known to exceed it.
    /// </summary>
    /// <remarks>
    /// Null rather than the source rate, so variable-framerate footage isn't
    /// forced to a constant rate. An unknown source rate isn't capped, since
    /// that could raise a lower one.
    /// </remarks>
    public static double? ResolveFps(double? requestedFps, double? sourceFps, double? maxFps)
    {
        if (requestedFps is { } requested)
        {
            return requested;
        }

        return maxFps is { } max && sourceFps is { } source && source > max ? max : null;
    }

    private static int RoundToEven(double value) =>
        Math.Max(2, (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2);
}

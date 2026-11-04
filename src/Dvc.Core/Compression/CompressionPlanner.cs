using Dvc.Core.Definitions;

namespace Dvc.Core.Compression;

/// <summary>
/// Works out what an encode produces and how, from a request and its probed
/// source, as pure functions. Planning runs in two steps, the output
/// (<c>PlanOutput</c>) and then the encode (<c>PlanEncode</c>), so the
/// output is known even when the encode can't be planned.
/// </summary>
public static class CompressionPlanner
{
    // Stands in for the output framerate in bitrate math when neither the
    // request nor the source supplies one.
    internal const double FallbackFps = 30;

    // ffmpeg takes the bitrate in whole kbps, and libx264 treats "-b:v 0k" as
    // unset, silently falling back to constant quality. Below 1kbps, a size
    // target can't be encoded at all.
    internal const long MinEncodableVideoBitrateBps = 1_000;

    /// <summary>
    /// The output dimensions, framerate, audio bitrate and preset. An unset
    /// short side or framerate follows the source within the request's
    /// default caps; a set one is used as given.
    /// </summary>
    public static OutputSettings PlanOutput(CompressionRequest request, SourceInfo source)
    {
        var shortSide = VideoEncodingCalculator.ResolveShortSide(
            request.ShortSide, Math.Min(source.Width, source.Height), request.DefaultMaxShortSide);
        var (width, height) = VideoEncodingCalculator.CalculateOutputDimensions(source.Width, source.Height, shortSide);

        var forcedFps = VideoEncodingCalculator.ResolveFps(request.Fps, source.Fps, request.DefaultMaxFps);
        var audioBitrateBps = VideoEncodingCalculator.ResolveAudioBitrateBps(source.HasAudio, source.AudioBitrateBps);

        return new OutputSettings(
            width, height, forcedFps ?? source.Fps, forcedFps is not null, audioBitrateBps, request.EncoderPreset);
    }

    /// <summary>
    /// The full plan: a constant-quality encode for a quality tier, or a
    /// bitrate for a target size, capped at the source's own bitrate.
    /// </summary>
    /// <remarks>
    /// Throws <c>CompressionFailedException</c> for a target size that can't
    /// be encoded: the source's duration is unknown, or the size leaves no
    /// room for video.
    /// </remarks>
    /// <param name="output">The output planned for this request and source (<c>PlanOutput</c>).</param>
    public static CompressionPlan PlanEncode(CompressionRequest request, SourceInfo source, OutputSettings output)
    {
        if ((request.TargetSizeBytes is null) == (request.QualityPreference is null))
        {
            throw new ArgumentException(
                "Exactly one of a target size and a quality preference must be set.", nameof(request));
        }

        var effectiveFps = output.Fps ?? FallbackFps;

        // A quality tier is a constant-quality encode with no size to hit.
        if (request.QualityPreference is { } qualityPreference)
        {
            var crf = QualityPreferenceDefinitions.ResolveCrf(qualityPreference, output.Width, output.Height);
            var maxRateBps = VideoEncodingCalculator.CrfMaxRateBps(
                output.Width, output.Height, effectiveFps, source.VideoBitrateBps);

            return new CompressionPlan(
                request.InputPath, source, output, new ConstantQualityEncode(qualityPreference, crf, maxRateBps), []);
        }

        var targetSizeBytes = request.TargetSizeBytes!.Value;

        // A target size needs the source's duration to become a bitrate.
        if (source.DurationSeconds <= 0)
        {
            throw new CompressionFailedException(
                "Could not determine the video's duration, which a target size needs. " +
                "Use a quality preference instead, or re-export the source.");
        }

        var rawBitrateBps = VideoEncodingCalculator.BitrateFromTargetSize(
            targetSizeBytes, source.DurationSeconds, output.AudioBitrateBps);
        var requestedSize = ByteFormatting.Format(targetSizeBytes);

        if (rawBitrateBps < MinEncodableVideoBitrateBps)
        {
            throw new CompressionFailedException(output.AudioBitrateBps > 0
                ? $"The requested target of {requestedSize} is too small for this {source.DurationSeconds:F1}s video: " +
                  $"the audio track alone (~{output.AudioBitrateBps / 1000}kbps) needs nearly all of it, leaving no room " +
                  "for video. Try a larger target size."
                : $"The requested target of {requestedSize} is too small for this {source.DurationSeconds:F1}s video " +
                  "to encode at all. Try a larger target size.");
        }

        var (targetBitrateBps, cappedAtSource) = VideoEncodingCalculator.CapAtSourceBitrate(
            rawBitrateBps, source.VideoBitrateBps);

        List<string> warnings = [];
        if (cappedAtSource)
        {
            warnings.Add(
                $"The target of {requestedSize} allows ~{rawBitrateBps / 1000}kbps of video, more than the source's " +
                $"own ~{targetBitrateBps / 1000}kbps. Encoded at the source's bitrate instead - going above it can't " +
                "add quality, only inflate the file - so the output will be smaller than the target.");
        }

        return new CompressionPlan(
            request.InputPath, source, output,
            new TargetSizeEncode(targetSizeBytes, targetBitrateBps, request.TwoPass), warnings);
    }
}

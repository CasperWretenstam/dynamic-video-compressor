using System.Globalization;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Logging;

namespace Dvc.Core.Compression;

public class VideoCompressionService(ILogger<VideoCompressionService> logger) : IVideoCompressionService
{
    // VBV rate control: -maxrate/-bufsize limit how far the encoder can
    // spike above the average bitrate, reducing (not eliminating) overshoot.
    // Conventional libx264 starting values, not tuned by measurement.
    private const double MaxRateMultiplier = 1.5;
    private const double BufSizeMultiplier = 2.0;

    // Neither ABR mode guarantees an exact size, and an oversized file
    // defeats a size target (e.g. a strict upload limit). An overshoot is
    // re-encoded at a lower bitrate, at most this many times before the
    // compression fails.
    private const int MaxSizeCorrectionAttempts = 2;

    // Shave slightly more than the measured overshoot so a near-miss
    // doesn't need yet another attempt.
    private const double OvershootCorrectionMargin = 1.1;

    // ffmpeg's null muxer needs an OS-specific discard target.
    private static readonly string NullOutputPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    public async Task<SourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        IMediaAnalysis mediaInfo;
        try
        {
            mediaInfo = await FFProbe.AnalyseAsync(inputPath, cancellationToken: cancellationToken);
        }
        // FFMpegCore reports a non-zero ffprobe exit as FFMpegException, and
        // unparseable ffprobe output as FFProbeException.
        catch (Exception ex) when (ex is FFMpegException or FFProbeException)
        {
            throw new CompressionFailedException(
                "The file could not be read as a video. It may be corrupt or in an unsupported format.", ex);
        }

        var videoStream = mediaInfo.PrimaryVideoStream
            ?? throw new CompressionFailedException("The file has no video stream.");
        var audioStream = mediaInfo.PrimaryAudioStream;

        var (width, height) = VideoEncodingCalculator.DisplayDimensions(
            videoStream.Width, videoStream.Height, videoStream.Rotation);

        double? fps = videoStream.AvgFrameRate > 0 ? videoStream.AvgFrameRate
            : videoStream.FrameRate > 0 ? videoStream.FrameRate
            : null;

        // The stream's own bitrate, which ffprobe omits for some containers;
        // otherwise the container total minus the audio it'll be encoded at.
        // Null if neither is usable, rather than risk a wrong value capping
        // the bitrate.
        long? videoBitrateBps;
        if (videoStream.BitRate > 0)
        {
            videoBitrateBps = videoStream.BitRate;
        }
        else
        {
            var audioBitrateBps = VideoEncodingCalculator.ResolveAudioBitrateBps(audioStream is not null, audioStream?.BitRate);
            var estimatedBps = (long)mediaInfo.Format.BitRate - audioBitrateBps;
            videoBitrateBps = estimatedBps > 0 ? estimatedBps : null;
        }

        return new SourceInfo
        {
            DurationSeconds = mediaInfo.Duration.TotalSeconds,
            Width = width,
            Height = height,
            Fps = fps,
            HasAudio = audioStream is not null,
            AudioBitrateBps = audioStream is { BitRate: > 0 } ? audioStream.BitRate : null,
            VideoBitrateBps = videoBitrateBps,
        };
    }

    public async Task<CompressionResult> EncodeAsync(
        CompressionPlan plan, string outputPath, CancellationToken cancellationToken)
    {
        foreach (var warning in plan.Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        switch (plan.Mode)
        {
            case ConstantQualityEncode mode:
                await EncodeConstantQualityAsync(plan, mode, outputPath, cancellationToken);
                return new CompressionResult([], new FileInfo(outputPath).Length);

            case TargetSizeEncode mode:
                return await EncodeToTargetSizeAsync(plan, mode, outputPath, cancellationToken);

            default:
                throw new ArgumentException($"Unknown encode mode {plan.Mode.GetType().Name}.", nameof(plan));
        }
    }

    private async Task EncodeConstantQualityAsync(
        CompressionPlan plan, ConstantQualityEncode mode, string outputPath, CancellationToken cancellationToken)
    {
        var source = plan.Source;
        var output = plan.Output;
        var bufSizeBps = (long)(mode.MaxRateBps * BufSizeMultiplier);

        logger.LogInformation(
            "Compressing: {DurationSeconds}s {SourceWidth}x{SourceHeight} {SourceFps}fps source -> {QualityPreference} (CRF {Crf}) @ {Width}x{Height}, {Fps} ({EncoderPreset}, maxrate {MaxRateBps}bps, bufsize {BufSizeBps}bps)",
            source.DurationSeconds, source.Width, source.Height, source.Fps, mode.QualityPreference, mode.Crf,
            output.Width, output.Height,
            output.ForceFps ? $"{output.Fps!.Value.ToString(CultureInfo.InvariantCulture)}fps" : "source fps",
            output.EncoderPreset, mode.MaxRateBps, bufSizeBps);

        await EncodeCrfAsync(plan.InputPath, outputPath, output, mode.Crf, mode.MaxRateBps, bufSizeBps, cancellationToken);
    }

    private async Task<CompressionResult> EncodeToTargetSizeAsync(
        CompressionPlan plan, TargetSizeEncode mode, string outputPath, CancellationToken cancellationToken)
    {
        var output = plan.Output;
        var durationSeconds = plan.Source.DurationSeconds;
        var requestedSizeBytes = mode.TargetSizeBytes;
        var requestedSize = ByteFormatting.Format(requestedSizeBytes);
        var targetBitrateBps = mode.VideoBitrateBps;

        var effectiveFps = output.Fps ?? CompressionPlanner.FallbackFps;
        var outputDescription = $"{output.Width}x{output.Height}@{effectiveFps:0.##}fps";

        long actualSizeBytes;
        var attempt = 0;
        while (true)
        {
            attempt++;

            var maxRateBps = (long)(targetBitrateBps * MaxRateMultiplier);
            var bufSizeBps = (long)(maxRateBps * BufSizeMultiplier);

            logger.LogInformation(
                "Compressing (attempt {Attempt}): {DurationSeconds}s source -> target {TargetBitrateBps}bps video @ {Output} ({PassMode}, {EncoderPreset}, maxrate {MaxRateBps}bps, bufsize {BufSizeBps}bps)",
                attempt, durationSeconds, targetBitrateBps, outputDescription, mode.TwoPass ? "two-pass" : "one-pass",
                output.EncoderPreset, maxRateBps, bufSizeBps);

            if (mode.TwoPass)
            {
                await EncodeTwoPassAsync(plan.InputPath, outputPath, output, targetBitrateBps, maxRateBps, bufSizeBps, cancellationToken);
            }
            else
            {
                await EncodeOnePassAsync(plan.InputPath, outputPath, output, targetBitrateBps, maxRateBps, bufSizeBps, cancellationToken);
            }

            actualSizeBytes = new FileInfo(outputPath).Length;

            if (actualSizeBytes <= requestedSizeBytes)
            {
                break;
            }

            if (attempt > MaxSizeCorrectionAttempts)
            {
                throw new CompressionFailedException(
                    $"Could not encode the video within the requested target of {requestedSize} after " +
                    $"{attempt} attempts (last attempt produced {ByteFormatting.Format(actualSizeBytes)}). Try a lower " +
                    "resolution, fps, or a larger target size.");
            }

            // Lower the bitrate by the measured overshoot plus a margin. It
            // only goes down, so the source cap still holds. If nothing is
            // left, the encoder can't get small enough at this resolution/fps.
            var overshootBps = (long)((actualSizeBytes - requestedSizeBytes) * 8.0 / durationSeconds);
            targetBitrateBps -= (long)(overshootBps * OvershootCorrectionMargin);

            if (targetBitrateBps < CompressionPlanner.MinEncodableVideoBitrateBps)
            {
                throw new CompressionFailedException(
                    $"Could not encode the video within the requested target of {requestedSize} at " +
                    $"{outputDescription}: even the lowest bitrate the encoder can manage there came out at " +
                    $"{ByteFormatting.Format(actualSizeBytes)}. Try a lower resolution, fps, or a larger target size.");
            }

            logger.LogWarning(
                "Output size ({ActualSizeBytes} bytes) exceeded the requested target ({RequestedSizeBytes} bytes); " +
                "retrying at a reduced bitrate (attempt {NextAttempt}/{TotalAttempts})",
                actualSizeBytes, requestedSizeBytes, attempt + 1, MaxSizeCorrectionAttempts + 1);
        }

        // Reported only on success, since a warning means "completed, but
        // not exactly as requested". Individual retries stay in the log.
        if (attempt == 1)
        {
            return new CompressionResult([], actualSizeBytes);
        }

        var correctionWarning =
            $"Output size initially exceeded the requested target of {requestedSize}; corrected after " +
            $"{attempt - 1} additional encode(s) to {ByteFormatting.Format(actualSizeBytes)}.";
        logger.LogWarning("{Warning}", correctionWarning);

        return new CompressionResult([correctionWarning], actualSizeBytes);
    }

    // Single constant-quality pass: the encoder holds a perceived quality
    // level and spends whatever bitrate each scene needs to reach it.
    // maxrate/bufsize only cap the worst case on unusually complex content.
    private static async Task EncodeCrfAsync(
        string inputPath, string outputPath, OutputSettings settings, int crf, long maxRateBps, long bufSizeBps,
        CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(inputPath)
            .OutputToFile(outputPath, overwrite: true, options => ApplyAudioSettings(ApplyVideoSettings(options, settings), settings)
                .WithConstantRateFactor(crf)
                .WithCustomArgument($"-maxrate {maxRateBps}")
                .WithCustomArgument($"-bufsize {bufSizeBps}")
                .WithFastStart())
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously();
    }

    // Pass 1 measures per-frame complexity (video only, output discarded);
    // pass 2 uses it, via the passlog file, to spread the bit budget while
    // averaging to the target bitrate.
    private static async Task EncodeTwoPassAsync(
        string inputPath, string outputPath, OutputSettings settings, long targetBitrateBps, long maxRateBps, long bufSizeBps,
        CancellationToken cancellationToken)
    {
        // Unique per encode, so concurrent encodes never share a passlog.
        var passLogPrefix = Path.Combine(Path.GetTempPath(), $"dvc-2pass-{Guid.NewGuid()}");

        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(NullOutputPath, overwrite: true, options => ApplyVideoSettings(options, settings)
                    .WithVideoBitrate((int)(targetBitrateBps / 1000))
                    .WithCustomArgument("-an")
                    .WithCustomArgument("-pass 1")
                    .WithCustomArgument($"-passlogfile \"{passLogPrefix}\"")
                    .WithCustomArgument($"-maxrate {maxRateBps}")
                    .WithCustomArgument($"-bufsize {bufSizeBps}")
                    .ForceFormat("null"))
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously();

            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, overwrite: true, options => ApplyAudioSettings(ApplyVideoSettings(options, settings), settings)
                    .WithVideoBitrate((int)(targetBitrateBps / 1000))
                    .WithCustomArgument("-pass 2")
                    .WithCustomArgument($"-passlogfile \"{passLogPrefix}\"")
                    .WithCustomArgument($"-maxrate {maxRateBps}")
                    .WithCustomArgument($"-bufsize {bufSizeBps}")
                    .WithFastStart())
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously();
        }
        finally
        {
            File.Delete($"{passLogPrefix}-0.log");
            File.Delete($"{passLogPrefix}-0.log.mbtree");
        }
    }

    // Single ABR pass: the encoder spreads bits using only a running average,
    // without seeing the whole clip first. Faster than two-pass, but more
    // prone to misallocating bits between simple and complex scenes.
    private static async Task EncodeOnePassAsync(
        string inputPath, string outputPath, OutputSettings settings, long targetBitrateBps, long maxRateBps, long bufSizeBps,
        CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(inputPath)
            .OutputToFile(outputPath, overwrite: true, options => ApplyAudioSettings(ApplyVideoSettings(options, settings), settings)
                .WithVideoBitrate((int)(targetBitrateBps / 1000))
                .WithCustomArgument($"-maxrate {maxRateBps}")
                .WithCustomArgument($"-bufsize {bufSizeBps}")
                .WithFastStart())
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously();
    }

    // Codec, preset, size and framerate shared by every encode. Framerate is
    // only forced when it changes, so variable-framerate sources keep their
    // timing otherwise.
    private static FFMpegArgumentOptions ApplyVideoSettings(FFMpegArgumentOptions options, OutputSettings settings)
    {
        options
            .WithVideoCodec(VideoCodec.LibX264)
            .WithCustomArgument($"-preset {settings.EncoderPreset}")
            .Resize(settings.Width, settings.Height);

        if (settings.ForceFps && settings.Fps is { } fps)
        {
            options.WithCustomArgument($"-r {fps.ToString(CultureInfo.InvariantCulture)}");
        }

        return options;
    }

    // AAC at the resolved bitrate, or no audio track for a silent source.
    private static FFMpegArgumentOptions ApplyAudioSettings(FFMpegArgumentOptions options, OutputSettings settings) =>
        settings.AudioBitrateBps > 0
            ? options.WithAudioCodec(AudioCodec.Aac).WithAudioBitrate((int)(settings.AudioBitrateBps / 1000))
            : options.WithCustomArgument("-an");
}

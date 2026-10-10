using System.Globalization;
using Dvc.Api.Models.Definitions;
using Dvc.Api.Models.Entities;
using Dvc.Api.Models.Options;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using Microsoft.Extensions.Options;

namespace Dvc.Api.Services;

public class VideoCompressionService(
    IOptions<EncodingOptions> encodingOptions,
    ILogger<VideoCompressionService> logger) : IVideoCompressionService
{
    // Stands in for the output framerate in bitrate math when neither the
    // job nor ffprobe supplies one.
    private const double FallbackFps = 30;

    // VBV rate control: -maxrate/-bufsize limit how far the encoder can
    // spike above the average bitrate, reducing (not eliminating) overshoot.
    // Conventional libx264 starting values, not tuned by measurement.
    private const double MaxRateMultiplier = 1.5;
    private const double BufSizeMultiplier = 2.0;

    // Neither ABR mode guarantees an exact size, and an oversized file
    // defeats a size target (e.g. a strict upload limit). An overshoot is
    // re-encoded at a lower bitrate, at most this many times before the job
    // fails.
    private const int MaxSizeCorrectionAttempts = 2;

    // Shave slightly more than the measured overshoot so a near-miss
    // doesn't need yet another attempt.
    private const double OvershootCorrectionMargin = 1.1;

    // ffmpeg takes the bitrate in whole kbps, and libx264 treats "-b:v 0k" as
    // unset, silently falling back to constant quality. Below 1kbps, a size
    // target can't be encoded at all.
    private const long MinEncodableVideoBitrateBps = 1_000;

    // ffmpeg's null muxer needs an OS-specific discard target.
    private static readonly string NullOutputPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>The output settings every encode of a job shares.</summary>
    /// <param name="Fps">Null to keep the source's own timing as-is.</param>
    /// <param name="AudioBitrateBps">0 when the source has no audio stream.</param>
    private sealed record OutputSettings(int Width, int Height, double? Fps, string EncoderPreset, long AudioBitrateBps);

    public async Task CompressAsync(Job job, string outputPath, CancellationToken cancellationToken)
    {
        IMediaAnalysis mediaInfo;
        try
        {
            mediaInfo = await FFProbe.AnalyseAsync(job.InputPath, cancellationToken: cancellationToken);
        }
        // FFMpegCore reports a non-zero ffprobe exit as FFMpegException, and
        // unparseable ffprobe output as FFProbeException.
        catch (Exception ex) when (ex is FFMpegException or FFProbeException)
        {
            throw new JobFailedException(
                "The uploaded file could not be read as a video. It may be corrupt or in an unsupported format.", ex);
        }

        var videoStream = mediaInfo.PrimaryVideoStream
            ?? throw new JobFailedException("The uploaded file has no video stream.");

        var durationSeconds = mediaInfo.Duration.TotalSeconds;
        var audioStream = mediaInfo.PrimaryAudioStream;
        var audioBitrateBps = VideoEncodingCalculator.ResolveAudioBitrateBps(audioStream is not null, audioStream?.BitRate);

        // An unset resolution or framerate follows the source within the
        // configured caps; a set one is used as given.
        var options = encodingOptions.Value;
        var (sourceWidth, sourceHeight) = VideoEncodingCalculator.DisplayDimensions(
            videoStream.Width, videoStream.Height, videoStream.Rotation);
        var requestedShortSide = job.Resolution is null ? (int?)null : ResolutionDefinitions.ShortSides[job.Resolution];
        var shortSide = VideoEncodingCalculator.ResolveShortSide(
            requestedShortSide, Math.Min(sourceWidth, sourceHeight), options.DefaultMaxShortSide);
        var (width, height) = VideoEncodingCalculator.CalculateOutputDimensions(sourceWidth, sourceHeight, shortSide);

        double? sourceFps = videoStream.AvgFrameRate > 0 ? videoStream.AvgFrameRate
            : videoStream.FrameRate > 0 ? videoStream.FrameRate
            : null;
        var outputFps = VideoEncodingCalculator.ResolveFps(job.Fps, sourceFps, options.DefaultMaxFps);
        var effectiveFps = outputFps ?? sourceFps ?? FallbackFps;

        var settings = new OutputSettings(width, height, outputFps, job.EncoderPreset, audioBitrateBps);

        // Saved even if the job fails, so the caller sees what was attempted.
        job.OutputWidth = width;
        job.OutputHeight = height;
        job.OutputFps = outputFps ?? sourceFps;

        // The stream's own bitrate, which ffprobe omits for some containers;
        // otherwise the container total minus audio. Null if neither is
        // usable, rather than risk a wrong value capping the bitrate.
        long? sourceVideoBitrateBps;
        if (videoStream.BitRate > 0)
        {
            sourceVideoBitrateBps = videoStream.BitRate;
        }
        else
        {
            var estimatedBps = (long)mediaInfo.Format.BitRate - audioBitrateBps;
            sourceVideoBitrateBps = estimatedBps > 0 ? estimatedBps : null;
        }

        // A quality tier is a constant-quality encode with no size to hit,
        // so the bitrate targeting and size correction below don't apply.
        if (job.TargetSizeMb is not { } requestedSizeMb)
        {
            var crf = QualityPreferenceDefinitions.ResolveCrf(job.QualityPreference!, width, height);
            var crfMaxRateBps = VideoEncodingCalculator.CrfMaxRateBps(width, height, effectiveFps, sourceVideoBitrateBps);
            var crfBufSizeBps = (long)(crfMaxRateBps * BufSizeMultiplier);

            logger.LogInformation(
                "Compressing job {JobId}: {DurationSeconds}s {SourceWidth}x{SourceHeight} {SourceFps}fps source -> {QualityPreference} (CRF {Crf}) @ {Width}x{Height}, {Fps} ({EncoderPreset}, maxrate {MaxRateBps}bps, bufsize {BufSizeBps}bps)",
                job.Id, durationSeconds, sourceWidth, sourceHeight, sourceFps, job.QualityPreference, crf, width, height,
                outputFps is { } fps ? $"{fps.ToString(CultureInfo.InvariantCulture)}fps" : "source fps",
                settings.EncoderPreset, crfMaxRateBps, crfBufSizeBps);

            await EncodeCrfAsync(job, outputPath, settings, crf, crfMaxRateBps, crfBufSizeBps, cancellationToken);
            return;
        }

        // A target size needs the source's duration to become a bitrate.
        if (durationSeconds <= 0)
        {
            throw new JobFailedException(
                "Could not determine the uploaded video's duration, which a target size needs. " +
                "Use a quality preference instead, or re-export the source.");
        }

        var rawBitrateBps = VideoEncodingCalculator.BitrateFromTargetSize(requestedSizeMb, durationSeconds, audioBitrateBps);

        if (rawBitrateBps < MinEncodableVideoBitrateBps)
        {
            throw new JobFailedException(audioBitrateBps > 0
                ? $"The requested {requestedSizeMb:F2}MB target is too small for this {durationSeconds:F1}s video: " +
                  $"the audio track alone (~{audioBitrateBps / 1000}kbps) needs nearly all of it, leaving no room " +
                  "for video. Try a larger target size."
                : $"The requested {requestedSizeMb:F2}MB target is too small for this {durationSeconds:F1}s video " +
                  "to encode at all. Try a larger target size.");
        }

        var (targetBitrateBps, cappedAtSource) = VideoEncodingCalculator.CapAtSourceBitrate(rawBitrateBps, sourceVideoBitrateBps);

        var outputDescription = $"{width}x{height}@{effectiveFps:0.##}fps";

        if (cappedAtSource)
        {
            // Appended rather than overwritten, since the job may already
            // carry a warning from submission.
            var capWarning =
                $"The {requestedSizeMb:F2}MB target allows ~{rawBitrateBps / 1000}kbps of video, more than the source's " +
                $"own ~{targetBitrateBps / 1000}kbps. Encoded at the source's bitrate instead - going above it can't " +
                "add quality, only inflate the file - so the output will be smaller than the target.";
            job.Warning = job.Warning is null ? capWarning : $"{job.Warning} {capWarning}";
            logger.LogWarning("Job {JobId}: {Warning}", job.Id, capWarning);
        }

        var twoPass = options.TwoPass;

        double actualSizeMb;
        var attempt = 0;
        while (true)
        {
            attempt++;

            var maxRateBps = (long)(targetBitrateBps * MaxRateMultiplier);
            var bufSizeBps = (long)(maxRateBps * BufSizeMultiplier);

            logger.LogInformation(
                "Compressing job {JobId} (attempt {Attempt}): {DurationSeconds}s source -> target {TargetBitrateBps}bps video @ {Output} ({PassMode}, {EncoderPreset}, maxrate {MaxRateBps}bps, bufsize {BufSizeBps}bps)",
                job.Id, attempt, durationSeconds, targetBitrateBps, outputDescription, twoPass ? "two-pass" : "one-pass",
                settings.EncoderPreset, maxRateBps, bufSizeBps);

            if (twoPass)
            {
                await EncodeTwoPassAsync(job, outputPath, settings, targetBitrateBps, maxRateBps, bufSizeBps, cancellationToken);
            }
            else
            {
                await EncodeOnePassAsync(job, outputPath, settings, targetBitrateBps, maxRateBps, bufSizeBps, cancellationToken);
            }

            actualSizeMb = new FileInfo(outputPath).Length / 1024.0 / 1024.0;

            if (actualSizeMb <= requestedSizeMb)
            {
                break;
            }

            if (attempt > MaxSizeCorrectionAttempts)
            {
                throw new JobFailedException(
                    $"Could not encode the video within the requested {requestedSizeMb:F2}MB target after " +
                    $"{attempt} attempts (last attempt produced {actualSizeMb:F2}MB). Try a lower resolution, " +
                    "fps, or a larger target size.");
            }

            // Lower the bitrate by the measured overshoot plus a margin. It
            // only goes down, so the source cap still holds. If nothing is
            // left, the encoder can't get small enough at this resolution/fps.
            var overshootBps = (long)((actualSizeMb - requestedSizeMb) * 1024 * 1024 * 8 / durationSeconds);
            targetBitrateBps -= (long)(overshootBps * OvershootCorrectionMargin);

            if (targetBitrateBps < MinEncodableVideoBitrateBps)
            {
                throw new JobFailedException(
                    $"Could not encode the video within the requested {requestedSizeMb:F2}MB target at " +
                    $"{outputDescription}: even the lowest bitrate the encoder can manage there came out at " +
                    $"{actualSizeMb:F2}MB. Try a lower resolution, fps, or a larger target size.");
            }

            logger.LogWarning(
                "Job {JobId}: output size ({ActualSizeMb:F2}MB) exceeded the requested target " +
                "({RequestedSizeMb:F2}MB); retrying at a reduced bitrate (attempt {NextAttempt}/{TotalAttempts})",
                job.Id, actualSizeMb, requestedSizeMb, attempt + 1, MaxSizeCorrectionAttempts + 1);
        }

        // Recorded only on success, since a warning means "completed, but
        // not exactly as requested". Individual retries stay in the log.
        if (attempt > 1)
        {
            var correctionWarning =
                $"Output size initially exceeded the requested {requestedSizeMb:F2}MB target; corrected after " +
                $"{attempt - 1} additional encode(s) to {actualSizeMb:F2}MB.";
            job.Warning = job.Warning is null ? correctionWarning : $"{job.Warning} {correctionWarning}";
            logger.LogWarning("Job {JobId}: {Warning}", job.Id, correctionWarning);
        }
    }

    // Single constant-quality pass: the encoder holds a perceived quality
    // level and spends whatever bitrate each scene needs to reach it.
    // maxrate/bufsize only cap the worst case on unusually complex content.
    private static async Task EncodeCrfAsync(
        Job job, string outputPath, OutputSettings settings, int crf, long maxRateBps, long bufSizeBps,
        CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(job.InputPath)
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
        Job job, string outputPath, OutputSettings settings, long targetBitrateBps, long maxRateBps, long bufSizeBps,
        CancellationToken cancellationToken)
    {
        var passLogPrefix = Path.Combine(Path.GetTempPath(), $"dvc-2pass-{job.Id}");

        try
        {
            await FFMpegArguments
                .FromFileInput(job.InputPath)
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
                .FromFileInput(job.InputPath)
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
        Job job, string outputPath, OutputSettings settings, long targetBitrateBps, long maxRateBps, long bufSizeBps,
        CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(job.InputPath)
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

        if (settings.Fps is { } fps)
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

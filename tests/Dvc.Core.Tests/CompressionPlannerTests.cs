using Dvc.Core.Compression;
using Dvc.Core.Definitions;

namespace Dvc.Core.Tests;

public class CompressionPlannerTests
{
    // A 10s 1080p30 source with 128kbps audio and 8Mbps video.
    private static readonly SourceInfo Source = new()
    {
        DurationSeconds = 10,
        Width = 1920,
        Height = 1080,
        Fps = 30,
        HasAudio = true,
        AudioBitrateBps = 128_000,
        VideoBitrateBps = 8_000_000,
    };

    private static CompressionRequest Request(long? targetSizeBytes = null, string? qualityPreference = null) => new()
    {
        InputPath = "input.mp4",
        TargetSizeBytes = targetSizeBytes,
        QualityPreference = targetSizeBytes is null ? qualityPreference ?? QualityPreferenceDefinitions.Balanced : qualityPreference,
        EncoderPreset = "slow",
        TwoPass = true,
    };

    private static CompressionPlan Plan(CompressionRequest request, SourceInfo source) =>
        CompressionPlanner.PlanEncode(request, source, CompressionPlanner.PlanOutput(request, source));

    [Fact]
    public void PlanEncode_QualityPreferenceEncodesAtConstantQuality()
    {
        var plan = Plan(Request(qualityPreference: QualityPreferenceDefinitions.HigherQuality), Source);

        var mode = Assert.IsType<ConstantQualityEncode>(plan.Mode);
        Assert.Equal(QualityPreferenceDefinitions.HigherQuality, mode.QualityPreference);
        Assert.Equal(QualityPreferenceDefinitions.ResolveCrf(QualityPreferenceDefinitions.HigherQuality, 1920, 1080), mode.Crf);
        Assert.Equal(VideoEncodingCalculator.CrfMaxRateBps(1920, 1080, 30, 8_000_000), mode.MaxRateBps);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void PlanEncode_TargetSizeEncodesAtDerivedBitrate()
    {
        // 5MB over 10s leaves well under the source's 8Mbps for video.
        var plan = Plan(Request(targetSizeBytes: 5_000_000), Source);

        var mode = Assert.IsType<TargetSizeEncode>(plan.Mode);
        Assert.Equal(5_000_000, mode.TargetSizeBytes);
        Assert.Equal(VideoEncodingCalculator.BitrateFromTargetSize(5_000_000, 10, 128_000), mode.VideoBitrateBps);
        Assert.True(mode.TwoPass);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void PlanEncode_TargetAboveSourceBitrateIsCappedWithWarning()
    {
        // 50MB over 10s allows ~36Mbps of video, far above the source's 8Mbps.
        var plan = Plan(Request(targetSizeBytes: 50_000_000), Source);

        var mode = Assert.IsType<TargetSizeEncode>(plan.Mode);
        Assert.Equal(8_000_000, mode.VideoBitrateBps);
        Assert.Contains("source's bitrate", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void PlanEncode_TargetTooSmallForAudioFails()
    {
        // 100KB over 10s is 80kbps, less than the audio alone needs.
        var ex = Assert.Throws<CompressionFailedException>(() => Plan(Request(targetSizeBytes: 100_000), Source));

        Assert.Contains("audio track alone", ex.Message);
    }

    [Fact]
    public void PlanEncode_TargetTooSmallWithoutAudioFails()
    {
        var silent = Source with { HasAudio = false, AudioBitrateBps = null };

        var ex = Assert.Throws<CompressionFailedException>(() => Plan(Request(targetSizeBytes: 1_000), silent));

        Assert.Contains("to encode at all", ex.Message);
    }

    [Fact]
    public void PlanEncode_TargetSizeWithUnknownDurationFails()
    {
        var noDuration = Source with { DurationSeconds = 0 };

        var ex = Assert.Throws<CompressionFailedException>(() => Plan(Request(targetSizeBytes: 5_000_000), noDuration));

        Assert.Contains("duration", ex.Message);
    }

    [Fact]
    public void PlanEncode_QualityPreferenceWithUnknownDurationSucceeds()
    {
        var noDuration = Source with { DurationSeconds = 0 };

        var plan = Plan(Request(), noDuration);

        Assert.IsType<ConstantQualityEncode>(plan.Mode);
    }

    [Fact]
    public void PlanEncode_ThrowsUnlessExactlyOneOfTargetSizeAndQualityPreference()
    {
        var both = Request(targetSizeBytes: 5_000_000, qualityPreference: QualityPreferenceDefinitions.Balanced);
        var neither = Request(targetSizeBytes: 5_000_000) with { TargetSizeBytes = null };

        Assert.Throws<ArgumentException>(() => Plan(both, Source));
        Assert.Throws<ArgumentException>(() => Plan(neither, Source));
    }

    [Fact]
    public void PlanOutput_UnsetValuesFollowSourceWithinDefaultCaps()
    {
        var request = Request() with { DefaultMaxShortSide = 720, DefaultMaxFps = 24 };

        var output = CompressionPlanner.PlanOutput(request, Source);

        Assert.Equal((1280, 720), (output.Width, output.Height));
        Assert.Equal(24, output.Fps);
        Assert.True(output.ForceFps);
    }

    [Fact]
    public void PlanOutput_UnsetValuesBelowDefaultCapsKeepSource()
    {
        var request = Request() with { DefaultMaxShortSide = 2160, DefaultMaxFps = 60 };

        var output = CompressionPlanner.PlanOutput(request, Source);

        Assert.Equal((1920, 1080), (output.Width, output.Height));
        Assert.Equal(30, output.Fps); // the source's own, reported but not forced
        Assert.False(output.ForceFps);
    }

    [Fact]
    public void PlanOutput_ExplicitValuesIgnoreDefaultCaps()
    {
        var request = Request() with { ShortSide = 1440, Fps = 60, DefaultMaxShortSide = 720, DefaultMaxFps = 24 };

        var output = CompressionPlanner.PlanOutput(request, Source);

        Assert.Equal((2560, 1440), (output.Width, output.Height));
        Assert.Equal(60, output.Fps);
        Assert.True(output.ForceFps);
    }

    [Fact]
    public void PlanOutput_UnknownSourceFpsIsLeftUnset()
    {
        var unknownFps = Source with { Fps = null };

        var output = CompressionPlanner.PlanOutput(Request() with { DefaultMaxFps = 24 }, unknownFps);

        Assert.Null(output.Fps);
        Assert.False(output.ForceFps);
    }

    [Fact]
    public void PlanOutput_SourceWithoutAudioHasNoAudioBitrate()
    {
        var silent = Source with { HasAudio = false, AudioBitrateBps = null };

        var output = CompressionPlanner.PlanOutput(Request(), silent);

        Assert.Equal(0, output.AudioBitrateBps);
    }

    [Fact]
    public void PlanEncode_SourceWithoutAudioGivesVideoTheWholeTarget()
    {
        var silent = Source with { HasAudio = false, AudioBitrateBps = null };

        var plan = Plan(Request(targetSizeBytes: 5_000_000), silent);

        var mode = Assert.IsType<TargetSizeEncode>(plan.Mode);
        Assert.Equal(VideoEncodingCalculator.BitrateFromTargetSize(5_000_000, 10, 0), mode.VideoBitrateBps);
    }
}

using Dvc.Core.Compression;

namespace Dvc.Core.Tests;

public class VideoEncodingCalculatorTests
{
    [Theory]
    [InlineData(3840, 2160, 1080, 1920, 1080)] // 16:9 landscape downscale
    [InlineData(2160, 3840, 1080, 1080, 1920)] // portrait keeps its orientation
    [InlineData(1920, 1080, 480, 854, 480)]    // 853.33 -> rounded to even 854
    [InlineData(2560, 1080, 1080, 2560, 1080)] // 21:9 ultrawide, already at the short side
    [InlineData(1440, 1440, 1080, 1080, 1080)] // square
    [InlineData(1280, 720, 1080, 1920, 1080)]  // upscale when asked
    [InlineData(1279, 719, 719, 1280, 720)]    // odd source dimensions rounded to even
    public void CalculateOutputDimensions_PreservesAspectRatioAndOrientation(
        int sourceWidth, int sourceHeight, int shortSide, int expectedWidth, int expectedHeight)
    {
        var (width, height) = VideoEncodingCalculator.CalculateOutputDimensions(sourceWidth, sourceHeight, shortSide);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Equal(0, width % 2); // libx264 requires even dimensions
        Assert.Equal(0, height % 2);
    }

    [Theory]
    [InlineData(0, 1080, 1080)]
    [InlineData(1920, 0, 1080)]
    [InlineData(1920, 1080, 0)]
    public void CalculateOutputDimensions_ThrowsForNonPositiveInput(int sourceWidth, int sourceHeight, int shortSide)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoEncodingCalculator.CalculateOutputDimensions(sourceWidth, sourceHeight, shortSide));
    }

    [Theory]
    [InlineData(0, 1920, 1080)]
    [InlineData(180, 1920, 1080)]
    [InlineData(90, 1080, 1920)]
    [InlineData(-90, 1080, 1920)]
    [InlineData(270, 1080, 1920)]
    public void DisplayDimensions_SwapsForQuarterTurnRotations(int rotation, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), VideoEncodingCalculator.DisplayDimensions(1920, 1080, rotation));
    }

    [Theory]
    [InlineData(720, 2160, 1080, 720)]    // requested wins over the cap
    [InlineData(2160, 720, 1080, 2160)]   // requested is honored even when it upscales
    [InlineData(null, 2160, 1080, 1080)]  // unset: source capped
    [InlineData(null, 720, 1080, 720)]    // unset: cap never upscales
    [InlineData(null, 2160, null, 2160)]  // unset, cap disabled: source kept
    public void ResolveShortSide_HonorsRequestOtherwiseCapsSource(
        int? requested, int sourceShortSide, int? maxShortSide, int expected)
    {
        Assert.Equal(expected, VideoEncodingCalculator.ResolveShortSide(requested, sourceShortSide, maxShortSide));
    }

    [Theory]
    [InlineData(24.0, 60.0, 30.0, 24.0)]  // requested wins over the cap
    [InlineData(60.0, 30.0, 30.0, 60.0)]  // requested is honored even above the source
    [InlineData(null, 60.0, 30.0, 30.0)]  // unset: source above the cap is lowered
    [InlineData(null, 29.97, 30.0, null)] // unset: source within the cap keeps its own timing
    [InlineData(null, null, 30.0, null)]  // unset: unknown source isn't forced to the cap
    [InlineData(null, 60.0, null, null)]  // unset, cap disabled: source kept
    public void ResolveFps_HonorsRequestOtherwiseCapsSource(
        double? requested, double? sourceFps, double? maxFps, double? expected)
    {
        Assert.Equal(expected, VideoEncodingCalculator.ResolveFps(requested, sourceFps, maxFps));
    }

    [Theory]
    [InlineData(1920, 1080, 30, 12_000_000)] // reference point
    [InlineData(640, 360, 30, 2_069_127)]
    [InlineData(1280, 720, 30, 6_272_421)]
    [InlineData(2560, 1440, 30, 19_014_426)]
    [InlineData(3840, 2160, 30, 36_377_197)]
    [InlineData(1920, 1080, 60, 18_188_598)]
    public void MaxVideoBitrateBps_ScalesFromReference(int width, int height, double fps, long expectedBps)
    {
        Assert.Equal(expectedBps, VideoEncodingCalculator.MaxVideoBitrateBps(width, height, fps));
    }

    [Fact]
    public void MaxVideoBitrateBps_ScalesSubLinearly()
    {
        var reference = VideoEncodingCalculator.MaxVideoBitrateBps(1920, 1080, 30);

        Assert.True(VideoEncodingCalculator.MaxVideoBitrateBps(3840, 2160, 30) < reference * 4, "4x the pixels should cost well under 4x");
        Assert.True(VideoEncodingCalculator.MaxVideoBitrateBps(1920, 1080, 60) < reference * 2, "2x the fps should cost well under 2x");
    }

    [Fact]
    public void MaxVideoBitrateBps_IsOrientationIndependent()
    {
        Assert.Equal(
            VideoEncodingCalculator.MaxVideoBitrateBps(1920, 1080, 30),
            VideoEncodingCalculator.MaxVideoBitrateBps(1080, 1920, 30));
    }

    [Theory]
    [InlineData(0, 1080, 30)]
    [InlineData(1920, 1080, 0)]
    [InlineData(1920, 1080, -30)]
    public void MaxVideoBitrateBps_ThrowsForNonPositiveInput(int width, int height, double fps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoEncodingCalculator.MaxVideoBitrateBps(width, height, fps));
    }

    [Fact]
    public void CrfMaxRateBps_IsCappedAtSourceBitrate()
    {
        Assert.Equal(3_000_000, VideoEncodingCalculator.CrfMaxRateBps(3840, 2160, 60, sourceBitrateBps: 3_000_000));
    }

    [Fact]
    public void CrfMaxRateBps_IgnoresSourceBitrate_WhenAboveCeiling()
    {
        // A 4K60 phone recording at ~100Mbps shouldn't lift the ceiling.
        Assert.Equal(
            VideoEncodingCalculator.MaxVideoBitrateBps(3840, 2160, 60),
            VideoEncodingCalculator.CrfMaxRateBps(3840, 2160, 60, sourceBitrateBps: 100_000_000));
    }

    [Fact]
    public void CrfMaxRateBps_UsesCeiling_WhenSourceBitrateUnknown()
    {
        Assert.Equal(12_000_000, VideoEncodingCalculator.CrfMaxRateBps(1920, 1080, 30, sourceBitrateBps: null));
    }

    [Fact]
    public void BitrateFromTargetSize_ReservesAudioAndAppliesBufferCoefficient()
    {
        // 10 MiB target, 60s duration, 128 kbps audio reserved.
        // (10_485_760 * 8 / 60 - 128_000) * 0.9 = 1_143_091.2 -> 1_143_091
        var bitrate = VideoEncodingCalculator.BitrateFromTargetSize(
            targetSizeBytes: 10_485_760, durationSeconds: 60, audioBitrateBps: 128_000);

        Assert.Equal(1_143_091, bitrate);
    }

    [Fact]
    public void BitrateFromTargetSize_FloorsAtZero_WhenAudioBudgetExceedsTarget()
    {
        // Audio budget alone exceeds what the target size affords, so the raw
        // size-based math should bottom out at 0 rather than go negative.
        var bitrate = VideoEncodingCalculator.BitrateFromTargetSize(
            targetSizeBytes: 10_000, durationSeconds: 60, audioBitrateBps: 128_000);

        Assert.Equal(0, bitrate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BitrateFromTargetSize_ThrowsForNonPositiveDuration(double duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoEncodingCalculator.BitrateFromTargetSize(
                targetSizeBytes: 10_485_760, durationSeconds: duration, audioBitrateBps: 128_000));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(50_000)]
    [InlineData(500_000_000)] // far above any realistic bitrate
    public void CapAtSourceBitrate_PassesThroughUnchanged_WhenSourceBitrateUnknown(long requestedBps)
    {
        Assert.Equal((requestedBps, false), VideoEncodingCalculator.CapAtSourceBitrate(requestedBps, sourceBitrateBps: null));
    }

    [Fact]
    public void CapAtSourceBitrate_CapsToSourceBitrate()
    {
        // There's no detail in the source to justify encoding above it.
        Assert.Equal((100_000L, true), VideoEncodingCalculator.CapAtSourceBitrate(1_000_000, sourceBitrateBps: 100_000));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(500_000)]
    [InlineData(2_000_000)] // exactly at the source bitrate isn't above it
    public void CapAtSourceBitrate_NeverRaises_WhenAtOrBelowSource(long requestedBps)
    {
        Assert.Equal((requestedBps, false), VideoEncodingCalculator.CapAtSourceBitrate(requestedBps, sourceBitrateBps: 2_000_000));
    }

    [Theory]
    [InlineData(96_000L, 96_000L)]     // below the cap: kept as-is
    [InlineData(192_000L, 192_000L)]   // exactly at the cap
    [InlineData(320_000L, 192_000L)]   // high-bitrate lossy source
    [InlineData(1_536_000L, 192_000L)] // PCM/lossless source
    [InlineData(null, 128_000L)]       // stream present, bitrate unreported
    [InlineData(0L, 128_000L)]
    public void ResolveAudioBitrateBps_CapsSourceAndFallsBackWhenUnknown(long? sourceBps, long expectedBps)
    {
        Assert.Equal(expectedBps, VideoEncodingCalculator.ResolveAudioBitrateBps(hasAudioStream: true, sourceBps));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(128_000L)]
    public void ResolveAudioBitrateBps_ReservesNothing_WithoutAudioStream(long? sourceBps)
    {
        Assert.Equal(0, VideoEncodingCalculator.ResolveAudioBitrateBps(hasAudioStream: false, sourceBps));
    }
}

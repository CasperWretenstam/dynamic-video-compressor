using Dvc.Core.Compression;
using Dvc.Desktop.ViewModels;

namespace Dvc.Desktop.Tests;

public class DisplayTextTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(47_400_000, "45.2 MB")]
    [InlineData(157_286_400, "150 MB")]
    [InlineData(1_610_612_736, "1.5 GB")]
    public void FileSize_UsesTheLargestWholeUnit(long bytes, string expected)
    {
        Assert.Equal(expected, DisplayText.FileSize(bytes));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(42.4, "0:42")]
    [InlineData(59.6, "1:00")]
    [InlineData(754, "12:34")]
    [InlineData(3723, "1:02:03")]
    [InlineData(36_000, "10:00:00")]
    public void Duration_ShowsHoursOnlyFromAnHourUp(double seconds, string expected)
    {
        Assert.Equal(expected, DisplayText.Duration(seconds));
    }

    [Fact]
    public void SourceDetails_ListsResolutionDurationFramerateAndSize()
    {
        var info = new SourceInfo { DurationSeconds = 42, Width = 1920, Height = 1080, Fps = 29.97, HasAudio = true };

        Assert.Equal("1920×1080 · 0:42 · 29.97 fps · 45.2 MB", DisplayText.SourceDetails(info, 47_400_000));
    }

    [Fact]
    public void SourceDetails_LeavesOutWhatFfprobeCouldntTell()
    {
        var info = new SourceInfo { DurationSeconds = 0, Width = 1080, Height = 1920, Fps = null, HasAudio = false };

        Assert.Equal("1080×1920 · 1.0 KB", DisplayText.SourceDetails(info, 1024));
    }

    [Fact]
    public void SourceDetails_RoundsTheFramerateToTwoDecimals()
    {
        var info = new SourceInfo { DurationSeconds = 1, Width = 640, Height = 360, Fps = 24000.0 / 1001, HasAudio = false };

        Assert.Contains("23.98 fps", DisplayText.SourceDetails(info, 1));
    }

    [Theory]
    [InlineData(47_400_000, 12_687_769, "45.2 MB → 12.1 MB, 73% smaller")]
    [InlineData(1_048_576, 1_258_291, "1.0 MB → 1.2 MB, 20% larger than the original")]
    [InlineData(1_000_000, 1_002_000, "977 KB → 979 KB, about the same size as the original")]
    [InlineData(2048, 7, "2.0 KB → 7 bytes, 99% smaller")]
    [InlineData(2048, 0, "2.0 KB → 0 bytes, 100% smaller")]
    [InlineData(0, 7, "0 bytes → 7 bytes")]
    public void SizeChange_ComparesTheCompressedSizeWithTheOriginal(long original, long compressed, string expected)
    {
        Assert.Equal(expected, DisplayText.SizeChange(original, compressed));
    }

    [Fact]
    public void EstimateTimeRemaining_IsNullForTheFirstFewSeconds()
    {
        Assert.Null(DisplayText.EstimateTimeRemaining(0.5, DisplayText.TimeRemainingDelay - TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void EstimateTimeRemaining_IsNullBeforeAnyRealProgress()
    {
        Assert.Null(DisplayText.EstimateTimeRemaining(0.005, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void EstimateTimeRemaining_ExtrapolatesFromTheProgressSoFar()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DisplayText.EstimateTimeRemaining(0.25, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, DisplayText.EstimateTimeRemaining(1, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(0, "About 5 s left")]
    [InlineData(3, "About 5 s left")]
    [InlineData(42, "About 45 s left")]
    [InlineData(58, "About 1 min left")]
    [InlineData(150, "About 3 min left")]
    [InlineData(3_600, "About 1 h left")]
    [InlineData(4_800, "About 1 h 20 min left")]
    public void TimeRemaining_IsRoundedToSuitItsLength(double seconds, string expected)
    {
        Assert.Equal(expected, DisplayText.TimeRemaining(TimeSpan.FromSeconds(seconds)));
    }
}

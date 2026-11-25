using Dvc.Core.Compression;

namespace Dvc.Core.Tests;

public class EncodeProgressTests
{
    private const double Duration = 100;
    private const double FirstShare = EncodeProgress.FirstPassShare;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 0.25)]
    [InlineData(100, 1)]
    public void At_SinglePassIsThePositionThroughTheSource(double positionSeconds, double expected)
    {
        var progress = EncodeProgress.At(TimeSpan.FromSeconds(positionSeconds), Duration, EncodePass.Single, attempt: 1);

        Assert.Equal(expected, progress.Fraction!.Value, precision: 10);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, FirstShare / 2)]
    [InlineData(100, FirstShare)]
    public void At_FirstPassFillsTheFirstShare(double positionSeconds, double expected)
    {
        var progress = EncodeProgress.At(TimeSpan.FromSeconds(positionSeconds), Duration, EncodePass.First, attempt: 1);

        Assert.Equal(expected, progress.Fraction!.Value, precision: 10);
    }

    [Theory]
    [InlineData(0, FirstShare)]
    [InlineData(50, FirstShare + (1 - FirstShare) / 2)]
    [InlineData(100, 1)]
    public void At_SecondPassCarriesOnFromTheFirst(double positionSeconds, double expected)
    {
        var progress = EncodeProgress.At(TimeSpan.FromSeconds(positionSeconds), Duration, EncodePass.Second, attempt: 1);

        Assert.Equal(expected, progress.Fraction!.Value, precision: 10);
    }

    [Theory]
    [InlineData(EncodePass.Single)]
    [InlineData(EncodePass.First)]
    [InlineData(EncodePass.Second)]
    public void At_PositionOutsideTheDurationIsClamped(EncodePass pass)
    {
        // ffmpeg's last position can run slightly past the probed duration.
        var past = EncodeProgress.At(TimeSpan.FromSeconds(Duration + 5), Duration, pass, attempt: 1);
        var end = EncodeProgress.At(TimeSpan.FromSeconds(Duration), Duration, pass, attempt: 1);
        var before = EncodeProgress.At(TimeSpan.FromSeconds(-1), Duration, pass, attempt: 1);
        var start = EncodeProgress.At(TimeSpan.Zero, Duration, pass, attempt: 1);

        Assert.Equal(end.Fraction, past.Fraction);
        Assert.Equal(start.Fraction, before.Fraction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void At_UnknownDurationHasNoFraction(double durationSeconds)
    {
        var progress = EncodeProgress.At(TimeSpan.FromSeconds(10), durationSeconds, EncodePass.Single, attempt: 1);

        Assert.Null(progress.Fraction);
        Assert.Equal(EncodePass.Single, progress.Pass);
    }

    [Fact]
    public void At_ReRunStartsAgainFromZeroAndSaysWhichAttempt()
    {
        var progress = EncodeProgress.At(TimeSpan.Zero, Duration, EncodePass.First, attempt: 2);

        Assert.Equal(0, progress.Fraction);
        Assert.Equal(EncodePass.First, progress.Pass);
        Assert.Equal(2, progress.Attempt);
    }
}

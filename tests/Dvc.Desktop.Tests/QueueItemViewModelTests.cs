using Dvc.Core.Compression;
using Dvc.Desktop.ViewModels;

namespace Dvc.Desktop.Tests;

public class QueueItemViewModelTests
{
    private static QueueItemViewModel CompressingItem() => new("Videos/clip.mov") { Status = QueueItemStatus.Compressing };

    private static EncodeProgress Progress(double positionSeconds, EncodePass pass = EncodePass.Single, int attempt = 1) =>
        EncodeProgress.At(TimeSpan.FromSeconds(positionSeconds), durationSeconds: 100, pass, attempt);

    [Fact]
    public void BeforeAnyProgress_SaysItsCompressing()
    {
        var item = CompressingItem();

        Assert.False(item.IsProgressKnown);
        Assert.Equal("Compressing…", item.StatusText);
    }

    [Fact]
    public void APlainEncode_ShowsJustThePercentage()
    {
        var item = CompressingItem();

        item.ReportProgress(Progress(42.7), TimeSpan.FromSeconds(1));

        Assert.True(item.IsProgressKnown);
        Assert.Equal(42.7, item.ProgressPercent, precision: 6);
        Assert.Null(item.Phase);
        Assert.Equal("43%", item.StatusText);
    }

    [Fact]
    public void ThePercentage_OnlyReaches100AtTheEnd()
    {
        var item = CompressingItem();

        item.ReportProgress(Progress(99.6), TimeSpan.FromSeconds(1));
        Assert.Equal("99%", item.StatusText);

        item.ReportProgress(Progress(100), TimeSpan.FromSeconds(2));
        Assert.Equal("100%", item.StatusText);
    }

    [Fact]
    public void ATwoPassEncode_NamesEachPass()
    {
        var item = CompressingItem();

        item.ReportProgress(Progress(50, EncodePass.First), TimeSpan.FromSeconds(1));
        Assert.Equal("First pass · 20%", item.StatusText);

        item.ReportProgress(Progress(50, EncodePass.Second), TimeSpan.FromSeconds(2));
        Assert.Equal("Second pass · 70%", item.StatusText);
    }

    [Fact]
    public void ARedoneEncode_SaysItsCorrectingTheSize()
    {
        var item = CompressingItem();

        item.ReportProgress(Progress(10, EncodePass.Single, attempt: 2), TimeSpan.FromSeconds(1));

        Assert.Equal("Correcting size · 10%", item.StatusText);
    }

    [Fact]
    public void AnUnknownDuration_ShowsNoPercentage()
    {
        var item = CompressingItem();

        item.ReportProgress(new EncodeProgress(null, EncodePass.Single, 1), TimeSpan.FromSeconds(30));
        Assert.False(item.IsProgressKnown);
        Assert.Null(item.TimeRemaining);
        Assert.Equal("Compressing…", item.StatusText);

        item.ReportProgress(new EncodeProgress(null, EncodePass.Second, 1), TimeSpan.FromSeconds(31));
        Assert.Equal("Second pass", item.StatusText);
    }

    [Fact]
    public void TimeRemaining_AppearsOnceTheEstimateHasSettled()
    {
        var item = CompressingItem();

        item.ReportProgress(Progress(10), TimeSpan.FromSeconds(2));
        Assert.Null(item.TimeRemaining);

        item.ReportProgress(Progress(50), TimeSpan.FromSeconds(60));
        Assert.Equal("About 1 min left", item.TimeRemaining);
    }

    [Fact]
    public void TimeRemaining_ForARedoneEncode_CountsFromWhenItStarted()
    {
        var item = CompressingItem();
        item.ReportProgress(Progress(100), TimeSpan.FromSeconds(600));

        // The redo has taken 30 of the 630 seconds so far, for half its work.
        item.ReportProgress(Progress(0, attempt: 2), TimeSpan.FromSeconds(600));
        item.ReportProgress(Progress(50, attempt: 2), TimeSpan.FromSeconds(630));

        Assert.Equal("About 30 s left", item.TimeRemaining);
    }

    [Fact]
    public void ClearProgress_ResetsEverything()
    {
        var item = CompressingItem();
        item.ReportProgress(Progress(50, EncodePass.Second), TimeSpan.FromSeconds(60));

        item.ClearProgress();

        Assert.False(item.IsProgressKnown);
        Assert.Equal(0, item.ProgressPercent);
        Assert.Null(item.Phase);
        Assert.Null(item.TimeRemaining);
    }
}

using Dvc.Desktop.Services;
using Dvc.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class MainWindowViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private MainWindowViewModel CreateViewModel(FfmpegCheck? ffmpegCheck = null) => new(
        ffmpegCheck ?? new FfmpegCheck(NullLogger<FfmpegCheck>.Instance),
        new QueueViewModel(
            new FakeFilePicker(),
            new CompressionRunner(new FakeCompressionService(), NullLogger<CompressionRunner>.Instance, _folder.Path),
            NullLogger<QueueViewModel>.Instance));

    [Fact]
    public async Task CheckFfmpegAsync_WithFfmpegMissing_SaysWhatsMissingAndWhatToInstall()
    {
        var missing = $"dvc-missing-{Guid.NewGuid():N}";
        var viewModel = CreateViewModel(new FfmpegCheck(NullLogger<FfmpegCheck>.Instance, missing, missing));

        await viewModel.CheckFfmpegAsync();

        Assert.False(viewModel.IsFfmpegReady);
        Assert.False(viewModel.Queue.AddVideosCommand.CanExecute(null));
        Assert.Contains("ffmpeg wasn't found on PATH.", viewModel.Status);
        Assert.Contains("ffprobe wasn't found on PATH.", viewModel.Status);
        Assert.Contains("winget install Gyan.FFmpeg.Essentials", viewModel.Status);
    }

    [Fact]
    public void IsFfmpegReady_LetsTheQueueRun()
    {
        var viewModel = CreateViewModel();

        viewModel.IsFfmpegReady = true;

        Assert.True(viewModel.Queue.IsFfmpegReady);
        Assert.True(viewModel.Queue.AddVideosCommand.CanExecute(null));
    }
}

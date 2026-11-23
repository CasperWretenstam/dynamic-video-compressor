using Dvc.Desktop.Services;
using Dvc.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public async Task CheckFfmpegAsync_WithFfmpegMissing_SaysWhatsMissingAndWhatToInstall()
    {
        var missing = $"dvc-missing-{Guid.NewGuid():N}";
        var viewModel = new MainWindowViewModel(new FfmpegCheck(NullLogger<FfmpegCheck>.Instance, missing, missing));

        await viewModel.CheckFfmpegAsync();

        Assert.False(viewModel.IsFfmpegReady);
        Assert.Contains("ffmpeg wasn't found on PATH.", viewModel.Status);
        Assert.Contains("ffprobe wasn't found on PATH.", viewModel.Status);
        Assert.Contains("winget install Gyan.FFmpeg.Essentials", viewModel.Status);
    }
}

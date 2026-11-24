using Dvc.Core.Compression;
using Dvc.Desktop.Services;
using Dvc.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class MainWindowViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeCompressionService _compressionService = new();

    public void Dispose() => _folder.Dispose();

    private MainWindowViewModel CreateViewModel(FfmpegCheck? ffmpegCheck = null) => new(
        ffmpegCheck ?? new FfmpegCheck(NullLogger<FfmpegCheck>.Instance),
        new FakeFilePicker(),
        new CompressionRunner(_compressionService, NullLogger<CompressionRunner>.Instance, Path.Combine(_folder.Path, "Output")),
        NullLogger<MainWindowViewModel>.Instance);

    [Fact]
    public async Task CheckFfmpegAsync_WithFfmpegMissing_SaysWhatsMissingAndWhatToInstall()
    {
        var missing = $"dvc-missing-{Guid.NewGuid():N}";
        var viewModel = CreateViewModel(new FfmpegCheck(NullLogger<FfmpegCheck>.Instance, missing, missing));

        await viewModel.CheckFfmpegAsync();

        Assert.False(viewModel.IsFfmpegReady);
        Assert.False(viewModel.ChooseVideoCommand.CanExecute(null));
        Assert.Contains("ffmpeg wasn't found on PATH.", viewModel.Status);
        Assert.Contains("ffprobe wasn't found on PATH.", viewModel.Status);
        Assert.Contains("winget install Gyan.FFmpeg.Essentials", viewModel.Status);
    }

    [Fact]
    public async Task CompressAsync_OnSuccess_ShowsTheOutputPath()
    {
        var viewModel = CreateViewModel();

        await viewModel.CompressAsync(_folder.File("clip.mov"));

        Assert.Equal($"Done. Saved to {Path.Combine(_folder.Path, "Output", "clip.mp4")}", viewModel.Status);
        Assert.False(viewModel.IsCompressing);
    }

    [Fact]
    public async Task CompressAsync_WithAnExplainedFailure_ShowsItsMessage()
    {
        var viewModel = CreateViewModel();
        _compressionService.EncodeFailure = new CompressionFailedException("The file has no video stream.");

        await viewModel.CompressAsync(_folder.File("clip.mov"));

        Assert.Equal("Couldn't compress clip.mov. The file has no video stream.", viewModel.Status);
    }

    [Fact]
    public async Task CompressAsync_WithAnyOtherFailure_ShowsAGenericMessage()
    {
        var viewModel = CreateViewModel();
        _compressionService.EncodeFailure = new InvalidOperationException("ffmpeg version 8.0 Copyright (c) ... raw output");

        await viewModel.CompressAsync(_folder.File("clip.mov"));

        Assert.StartsWith("Couldn't compress clip.mov. The video could not be compressed.", viewModel.Status);
        Assert.DoesNotContain("raw output", viewModel.Status);
    }

    [Fact]
    public async Task CompressAsync_WhileRunning_DisablesChoosingAnotherVideo()
    {
        var viewModel = CreateViewModel();
        viewModel.IsFfmpegReady = true;
        _compressionService.EncodeUntilCancelled = true;

        var compression = viewModel.CompressAsync(_folder.File("clip.mov"));

        Assert.True(viewModel.IsCompressing);
        Assert.False(viewModel.ChooseVideoCommand.CanExecute(null));

        await viewModel.CancelCompressionAsync();
        await compression;

        Assert.True(viewModel.ChooseVideoCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelCompressionAsync_StopsTheCompressionAndRemovesItsOutput()
    {
        var viewModel = CreateViewModel();
        _compressionService.EncodeUntilCancelled = true;

        _ = viewModel.CompressAsync(_folder.File("clip.mov"));
        await viewModel.CancelCompressionAsync();

        Assert.False(viewModel.IsCompressing);
        Assert.Equal("Cancelled.", viewModel.Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder.Path, "Output")));
    }

    [Fact]
    public async Task CancelCompressionAsync_WithNothingRunning_DoesNothing()
    {
        var viewModel = CreateViewModel();

        await viewModel.CancelCompressionAsync();

        Assert.False(viewModel.IsCompressing);
    }
}

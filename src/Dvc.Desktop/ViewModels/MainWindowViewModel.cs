using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dvc.Core.Compression;
using Dvc.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace Dvc.Desktop.ViewModels;

/// <summary>
/// The main window's state: whether ffmpeg is usable, whether a video is
/// being compressed, and the status message shown to the user.
/// </summary>
public partial class MainWindowViewModel(
    FfmpegCheck ffmpegCheck,
    IVideoFilePicker filePicker,
    CompressionRunner compressionRunner,
    ILogger<MainWindowViewModel> logger) : ObservableObject
{
    // Replaces any failure that isn't a CompressionFailedException. Those
    // mostly carry ffmpeg's raw output, which is logged but not shown.
    private const string GenericFailureMessage =
        "The video could not be compressed. The file may be corrupt or use an unsupported codec.";

    // The running compression, so closing the window can cancel it and wait
    // for its cleanup. Only touched on the UI thread.
    private CancellationTokenSource? _compressionCancellation;
    private Task _compression = Task.CompletedTask;

    /// <summary>The message the window shows the user about what the app is doing or what's wrong.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Checking for ffmpeg…";

    /// <summary>
    /// Whether ffmpeg and ffprobe passed the startup check
    /// (<c>CheckFfmpegAsync</c>). False until it has run.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseVideoCommand))]
    public partial bool IsFfmpegReady { get; set; }

    /// <summary>Whether a video is being compressed. Only one runs at a time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseVideoCommand))]
    public partial bool IsCompressing { get; set; }

    /// <summary>
    /// Checks the ffmpeg setup, updating whether it's ready
    /// (<c>IsFfmpegReady</c>) and the status message (<c>Status</c>), which
    /// says what to install if something's missing.
    /// </summary>
    public async Task CheckFfmpegAsync()
    {
        var result = await ffmpegCheck.CheckAsync(CancellationToken.None);

        IsFfmpegReady = result.IsUsable;
        Status = result.IsUsable
            ? "Ready. Choose a video to compress."
            : string.Join(Environment.NewLine,
            [
                "Dynamic Video Compressor needs ffmpeg to compress videos:",
                .. result.Problems.Select(problem => $"• {problem}"),
                "",
                "Install an ffmpeg build that includes ffprobe and libx264, such as the essentials build from " +
                "gyan.dev (winget install Gyan.FFmpeg.Essentials). Make sure its bin folder is on PATH, then " +
                "restart the app.",
            ]);
    }

    /// <summary>
    /// Compresses the video at <c>inputPath</c> into the output folder,
    /// showing that it's running and then the outcome in the status message
    /// (<c>Status</c>). Never throws: a failure is shown as the status.
    /// </summary>
    public Task CompressAsync(string inputPath)
    {
        _compression = RunCompressionAsync(inputPath);
        return _compression;
    }

    /// <summary>
    /// Cancels the running compression, if any, and waits until ffmpeg has
    /// stopped and the unfinished output is deleted.
    /// </summary>
    public async Task CancelCompressionAsync()
    {
        if (_compressionCancellation is null)
        {
            return;
        }

        Status = "Cancelling…";
        await _compressionCancellation.CancelAsync();
        await _compression;
    }

    private bool CanChooseVideo() => IsFfmpegReady && !IsCompressing;

    // The command stays disabled while it runs, so the picker can't be
    // opened twice.
    [RelayCommand(CanExecute = nameof(CanChooseVideo))]
    private async Task ChooseVideoAsync()
    {
        var inputPath = await filePicker.PickVideoAsync();
        if (inputPath is not null)
        {
            await CompressAsync(inputPath);
        }
    }

    private async Task RunCompressionAsync(string inputPath)
    {
        using var cancellation = new CancellationTokenSource();
        _compressionCancellation = cancellation;
        IsCompressing = true;

        var fileName = Path.GetFileName(inputPath);
        Status = $"Compressing {fileName}…";

        try
        {
            var video = await compressionRunner.CompressAsync(inputPath, cancellation.Token);
            Status = string.Join(Environment.NewLine, [$"Done. Saved to {video.OutputPath}", .. video.Warnings]);
        }
        // Cancelling can surface as ffmpeg failing rather than as a
        // cancellation exception, so any exception after a cancel counts.
        catch (Exception ex) when (cancellation.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Compression of {InputPath} cancelled", inputPath);
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Compression of {InputPath} failed", inputPath);
            var message = ex is CompressionFailedException ? ex.Message : GenericFailureMessage;
            Status = $"Couldn't compress {fileName}. {message}";
        }
        finally
        {
            _compressionCancellation = null;
            IsCompressing = false;
        }
    }
}

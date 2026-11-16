using CommunityToolkit.Mvvm.ComponentModel;
using Dvc.Desktop.Services;

namespace Dvc.Desktop.ViewModels;

/// <summary>
/// The main window's state: whether ffmpeg is usable, and the status message
/// shown to the user.
/// </summary>
public partial class MainWindowViewModel(FfmpegCheck ffmpegCheck) : ObservableObject
{
    /// <summary>The message the window shows the user about what the app is doing or what's wrong.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Checking for ffmpeg…";

    /// <summary>
    /// Whether ffmpeg and ffprobe passed the startup check
    /// (<c>CheckFfmpegAsync</c>). False until it has run.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFfmpegReady { get; set; }

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
            ? "Ready. ffmpeg and ffprobe were found."
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
}

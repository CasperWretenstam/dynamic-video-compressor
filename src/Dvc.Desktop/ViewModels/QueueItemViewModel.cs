using CommunityToolkit.Mvvm.ComponentModel;

namespace Dvc.Desktop.ViewModels;

/// <summary>Where a video in the queue is up to.</summary>
public enum QueueItemStatus
{
    /// <summary>Waiting its turn, or stopped and put back to be compressed again.</summary>
    Waiting,

    /// <summary>Being compressed now. Only one video is at a time.</summary>
    Compressing,

    /// <summary>Compressed and saved to the output folder.</summary>
    Done,

    /// <summary>Couldn't be compressed. It can be retried.</summary>
    Failed,
}

/// <summary>One video in the compression queue, and what's happened to it so far.</summary>
public partial class QueueItemViewModel(string inputPath) : ObservableObject
{
    /// <summary>The full path of the video to compress.</summary>
    public string InputPath { get; } = inputPath;

    /// <summary>The video's file name, without its folder.</summary>
    public string FileName { get; } = Path.GetFileName(inputPath);

    /// <summary>Where the video is up to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsCompressing), nameof(IsDone), nameof(IsFailed))]
    public partial QueueItemStatus Status { get; set; }

    /// <summary>
    /// What the user should know about the outcome: where a compressed video
    /// was saved and any warnings, or why it failed. Null while there's
    /// nothing to say.
    /// </summary>
    [ObservableProperty]
    public partial string? Details { get; set; }

    /// <summary>The status as shown to the user.</summary>
    public string StatusText => Status switch
    {
        QueueItemStatus.Waiting => "Waiting",
        QueueItemStatus.Compressing => "Compressing…",
        QueueItemStatus.Done => "Done",
        _ => "Failed",
    };

    /// <summary>Whether the video is being compressed now.</summary>
    public bool IsCompressing => Status == QueueItemStatus.Compressing;

    /// <summary>Whether the video was compressed and saved.</summary>
    public bool IsDone => Status == QueueItemStatus.Done;

    /// <summary>Whether the video couldn't be compressed.</summary>
    public bool IsFailed => Status == QueueItemStatus.Failed;
}

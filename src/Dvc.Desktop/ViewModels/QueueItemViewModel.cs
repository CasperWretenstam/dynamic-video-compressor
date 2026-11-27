using CommunityToolkit.Mvvm.ComponentModel;
using Dvc.Core.Compression;
using Dvc.Desktop.Services;

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
    // The attempt the latest progress report belonged to, and how long the
    // compression had been running when that attempt started, so the time
    // remaining is measured from the attempt's own start. The first attempt
    // starts with the compression.
    private int _attempt = 1;
    private TimeSpan _attemptStartedAt;

    /// <summary>The full path of the video to compress.</summary>
    public string InputPath { get; } = inputPath;

    /// <summary>The video's file name, without its folder.</summary>
    public string FileName { get; } = Path.GetFileName(inputPath);

    /// <summary>Where the video is up to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsCompressing), nameof(IsDone), nameof(IsFailed))]
    public partial QueueItemStatus Status { get; set; }

    /// <summary>
    /// What ffprobe found about the video, and its size. Null until probing
    /// it has finished, and if probing failed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDetails))]
    public partial SourceVideo? Source { get; set; }

    /// <summary>
    /// The probe of the video started when it was added, or null if it hasn't
    /// started yet. It throws if the video can't be read.
    /// </summary>
    internal Task<SourceVideo>? SourceProbe { get; set; }

    /// <summary>
    /// The video's resolution, duration, framerate and size, such as
    /// "1920×1080 · 0:42 · 30 fps · 45.2 MB". Null until it's been probed.
    /// </summary>
    public string? SourceDetails => Source is { } source ? DisplayText.SourceDetails(source.Info, source.SizeBytes) : null;

    /// <summary>
    /// How the compressed video's size compares with the original's, such as
    /// "45.2 MB → 12.1 MB, 73% smaller". Null until the video is done.
    /// </summary>
    [ObservableProperty]
    public partial string? SizeChange { get; set; }

    /// <summary>
    /// What the user should know about the outcome: where a compressed video
    /// was saved and any warnings, or why it failed. Null while there's
    /// nothing to say.
    /// </summary>
    [ObservableProperty]
    public partial string? Details { get; set; }

    /// <summary>
    /// How far along the compression is, from 0 to 100. Only meaningful
    /// while compressing and the progress is known (<c>IsProgressKnown</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double ProgressPercent { get; set; }

    /// <summary>
    /// Whether the compression's progress can be measured. It can't before
    /// the first report from ffmpeg, or when ffprobe couldn't tell the
    /// video's duration.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsProgressKnown { get; set; }

    /// <summary>
    /// Which stage the compression is in when it isn't a single plain encode:
    /// "First pass" or "Second pass" of a two-pass encode, or "Correcting
    /// size" when an encode aiming for a file size came out too large and
    /// is being redone. Null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Phase { get; set; }

    /// <summary>
    /// Roughly how long the compression has left, such as "About 3 min
    /// left". Null until there's a steady enough estimate.
    /// </summary>
    [ObservableProperty]
    public partial string? TimeRemaining { get; set; }

    /// <summary>
    /// The status as shown to the user. While compressing, that's the
    /// percentage done, after the stage if there is one ("Second pass ·
    /// 73%").
    /// </summary>
    public string StatusText => Status switch
    {
        QueueItemStatus.Waiting => "Waiting",
        QueueItemStatus.Compressing => (Phase, IsProgressKnown) switch
        {
            (null, false) => "Compressing…",
            (null, true) => $"{RoundedPercent}%",
            ({ } phase, false) => phase,
            ({ } phase, true) => $"{phase} · {RoundedPercent}%",
        },
        QueueItemStatus.Done => "Done",
        _ => "Failed",
    };

    // To the nearest whole percent, but only 100 once the encode has
    // actually reached the end.
    private int RoundedPercent =>
        Math.Min((int)Math.Round(ProgressPercent, MidpointRounding.AwayFromZero), ProgressPercent < 100 ? 99 : 100);

    /// <summary>Whether the video is being compressed now.</summary>
    public bool IsCompressing => Status == QueueItemStatus.Compressing;

    /// <summary>Whether the video was compressed and saved.</summary>
    public bool IsDone => Status == QueueItemStatus.Done;

    /// <summary>Whether the video couldn't be compressed.</summary>
    public bool IsFailed => Status == QueueItemStatus.Failed;

    /// <summary>
    /// Shows a progress report from the compression, received
    /// <c>elapsed</c> after the compression started.
    /// </summary>
    public void ReportProgress(EncodeProgress progress, TimeSpan elapsed)
    {
        // A redone encode starts its progress again from 0, so its time
        // remaining is measured from when it started.
        if (progress.Attempt != _attempt)
        {
            _attempt = progress.Attempt;
            _attemptStartedAt = elapsed;
        }

        Phase = progress switch
        {
            { Attempt: > 1 } => "Correcting size",
            { Pass: EncodePass.First } => "First pass",
            { Pass: EncodePass.Second } => "Second pass",
            _ => null,
        };

        if (progress.Fraction is { } fraction)
        {
            IsProgressKnown = true;
            ProgressPercent = fraction * 100;
            TimeRemaining = DisplayText.EstimateTimeRemaining(fraction, elapsed - _attemptStartedAt) is { } remaining
                ? DisplayText.TimeRemaining(remaining)
                : null;
        }
        else
        {
            IsProgressKnown = false;
            TimeRemaining = null;
        }
    }

    /// <summary>Clears the progress shown, before a compression starts or once it ends.</summary>
    public void ClearProgress()
    {
        _attempt = 1;
        _attemptStartedAt = TimeSpan.Zero;
        ProgressPercent = 0;
        IsProgressKnown = false;
        Phase = null;
        TimeRemaining = null;
    }
}

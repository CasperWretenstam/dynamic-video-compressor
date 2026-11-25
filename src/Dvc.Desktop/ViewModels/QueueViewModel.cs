using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dvc.Core.Compression;
using Dvc.Core.Definitions;
using Dvc.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace Dvc.Desktop.ViewModels;

/// <summary>
/// The compression queue: the videos the user added, compressed one at a
/// time in order, with pausing, stopping, removing and retrying. It lives in
/// memory only, so it's gone when the app closes.
/// </summary>
public partial class QueueViewModel : ObservableObject
{
    // Replaces any failure that isn't a CompressionFailedException. Those
    // mostly carry ffmpeg's raw output, which is logged but not shown.
    private const string GenericFailureMessage =
        "The video could not be compressed. The file may be corrupt or use an unsupported codec.";

    // Windows and macOS file systems ignore case by default, so the same
    // file can arrive under differently cased paths there.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly IVideoFilePicker _filePicker;
    private readonly CompressionRunner _compressionRunner;
    private readonly ILogger<QueueViewModel> _logger;

    // The loop working through the queue, and the video it's compressing.
    // Only touched on the UI thread.
    private Task _processing = Task.CompletedTask;
    private bool _isProcessing;
    private Task _currentCompression = Task.CompletedTask;
    private CancellationTokenSource? _currentCancellation;

    // Set when the current video's compression is cancelled to remove it,
    // rather than to put it back in the queue.
    private bool _removeCurrentWhenStopped;

    public QueueViewModel(IVideoFilePicker filePicker, CompressionRunner compressionRunner, ILogger<QueueViewModel> logger)
    {
        _filePicker = filePicker;
        _compressionRunner = compressionRunner;
        _logger = logger;
        Items.CollectionChanged += (_, _) => OnQueueChanged();
    }

    /// <summary>The videos in the queue, in the order they're compressed.</summary>
    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    /// <summary>
    /// Whether ffmpeg passed the startup check. Videos can only be added,
    /// and the queue only runs, once it has.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddVideosCommand))]
    public partial bool IsFfmpegReady { get; set; }

    /// <summary>
    /// Whether the queue is paused: the current video, if any, finishes, but
    /// no new one starts until the queue is resumed.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsPaused { get; set; }

    /// <summary>The video being compressed now, or null if none is.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopNowCommand))]
    [NotifyPropertyChangedFor(nameof(IsCompressing), nameof(Summary))]
    public partial QueueItemViewModel? CurrentItem { get; set; }

    /// <summary>Whether a video is being compressed now.</summary>
    public bool IsCompressing => CurrentItem is not null;

    /// <summary>Whether the queue has no videos in it at all.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// A message about the last videos the user tried to add, naming any that
    /// were skipped and why. Null when there's nothing to say.
    /// </summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>A one-line description of what the queue is doing, for the status bar.</summary>
    public string Summary
    {
        get
        {
            var waiting = CountWaiting();
            var waitingText = waiting > 0 ? $" · {waiting} waiting" : "";

            if (CurrentItem is { } current)
            {
                return $"Compressing {current.FileName}{waitingText}{(IsPaused ? " · Pausing after this video" : "")}";
            }

            if (IsPaused)
            {
                return $"Paused{waitingText}";
            }

            return Items.Count == 0 ? "Add videos to start compressing." : "Queue finished.";
        }
    }

    /// <summary>
    /// What closing the app now would interrupt, worded as a warning to
    /// confirm. Null when nothing is waiting or being compressed, so the app
    /// can close without asking.
    /// </summary>
    public string? CloseWarning
    {
        get
        {
            var waiting = CountWaiting();
            return (CurrentItem, waiting) switch
            {
                (null, 0) => null,
                (null, _) => $"{VideosAre(waiting)} still waiting. Closing the app empties the queue.",
                ({ } current, 0) =>
                    $"{current.FileName} is still being compressed. Closing the app stops the compression.",
                ({ } current, _) =>
                    $"{current.FileName} is still being compressed, and {VideosAre(waiting)} waiting. " +
                    "Closing the app stops the compression and empties the queue.",
            };
        }
    }

    /// <summary>
    /// Adds the videos at <c>paths</c> to the end of the queue, and starts it
    /// if it's idle and not paused. Skips, and names in the notice
    /// (<c>Notice</c>), anything that isn't a supported video file and any
    /// video already waiting or being compressed.
    /// </summary>
    public void AddVideos(IEnumerable<string> paths)
    {
        var skipped = new List<string>();

        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);

            if (Directory.Exists(path))
            {
                skipped.Add($"{fileName} (a folder)");
            }
            else if (!VideoFileDefinitions.SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                skipped.Add($"{fileName} (not a supported video type)");
            }
            else if (Items.Any(item => item.Status is QueueItemStatus.Waiting or QueueItemStatus.Compressing
                && PathComparer.Equals(item.InputPath, path)))
            {
                skipped.Add($"{fileName} (already in the queue)");
            }
            else
            {
                Items.Add(new QueueItemViewModel(path));
            }
        }

        Notice = skipped.Count switch
        {
            0 => null,
            1 => $"Skipped {skipped[0]}.",
            _ => $"Skipped {skipped.Count} files: {string.Join(", ", skipped)}.",
        };

        StartProcessing();
    }

    /// <summary>
    /// Pauses the queue and stops the current video's compression straight
    /// away, putting that video back at the front of the queue. Completes
    /// once ffmpeg has stopped and the unfinished output is deleted.
    /// </summary>
    public async Task StopAsync()
    {
        IsPaused = true;

        if (_currentCancellation is { } cancellation)
        {
            _removeCurrentWhenStopped = false;
            await cancellation.CancelAsync();
        }

        await _currentCompression;
    }

    /// <summary>
    /// Completes once the queue has stopped working: every video is done or
    /// failed, or it's paused and the current video has finished.
    /// </summary>
    public Task WhenIdleAsync() => _processing;

    private bool CanAddVideos() => IsFfmpegReady;

    // The command stays disabled while it runs, so the picker can't be
    // opened twice.
    [RelayCommand(CanExecute = nameof(CanAddVideos))]
    private async Task AddVideosAsync()
    {
        var paths = await _filePicker.PickVideosAsync();
        if (paths.Count > 0)
        {
            AddVideos(paths);
        }
    }

    private bool CanPause() => !IsPaused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => IsPaused = true;

    private bool CanResume() => IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        IsPaused = false;
        StartProcessing();
    }

    private bool CanStopNow() => CurrentItem is not null;

    [RelayCommand(CanExecute = nameof(CanStopNow))]
    private Task StopNowAsync() => StopAsync();

    // Removing the video being compressed stops its compression; the queue
    // then carries on with the next one unless it's paused.
    [RelayCommand]
    private void Remove(QueueItemViewModel item)
    {
        if (item == CurrentItem)
        {
            _removeCurrentWhenStopped = true;
            _currentCancellation?.Cancel();
        }
        else
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void Retry(QueueItemViewModel item)
    {
        if (item.Status != QueueItemStatus.Failed)
        {
            return;
        }

        item.Status = QueueItemStatus.Waiting;
        item.Details = null;
        Items.Move(Items.IndexOf(item), Items.Count - 1);
        StartProcessing();
    }

    private bool CanClearFinished() => Items.Any(item => item.Status is QueueItemStatus.Done or QueueItemStatus.Failed);

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(item => item.Status is QueueItemStatus.Done or QueueItemStatus.Failed).ToList())
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    // Starts working through the queue, unless that's already happening or
    // the queue can't run.
    private void StartProcessing()
    {
        if (!_isProcessing && !IsPaused && IsFfmpegReady)
        {
            _processing = ProcessAsync();
        }
    }

    partial void OnIsFfmpegReadyChanged(bool value) => StartProcessing();

    // Compresses waiting videos in order until none are left or the queue
    // is paused. The pause is checked between videos, so the current one
    // always gets to finish.
    private async Task ProcessAsync()
    {
        _isProcessing = true;
        try
        {
            while (!IsPaused && Items.FirstOrDefault(item => item.Status == QueueItemStatus.Waiting) is { } item)
            {
                _currentCompression = CompressAsync(item);
                await _currentCompression;
            }
        }
        finally
        {
            _isProcessing = false;
        }
    }

    // Never throws: the outcome is recorded on the item.
    private async Task CompressAsync(QueueItemViewModel item)
    {
        using var cancellation = new CancellationTokenSource();
        _currentCancellation = cancellation;
        _removeCurrentWhenStopped = false;
        item.Status = QueueItemStatus.Compressing;
        item.Details = null;
        CurrentItem = item;
        OnQueueChanged();

        try
        {
            var video = await _compressionRunner.CompressAsync(item.InputPath, cancellation.Token);
            item.Status = QueueItemStatus.Done;
            item.Details = string.Join(Environment.NewLine, [$"Saved to {video.OutputPath}", .. video.Warnings]);
        }
        // Cancelling can surface as ffmpeg failing rather than as a
        // cancellation exception, so any exception after a cancel counts.
        catch (Exception ex) when (cancellation.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Compression of {InputPath} stopped", item.InputPath);
            item.Status = QueueItemStatus.Waiting;
            if (_removeCurrentWhenStopped)
            {
                Items.Remove(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compression of {InputPath} failed", item.InputPath);
            item.Status = QueueItemStatus.Failed;
            item.Details = ex is CompressionFailedException ? ex.Message : GenericFailureMessage;
        }
        finally
        {
            _currentCancellation = null;
            CurrentItem = null;
            OnQueueChanged();
        }
    }

    // Refreshes everything worked out from the items' statuses.
    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    private int CountWaiting() => Items.Count(item => item.Status == QueueItemStatus.Waiting);

    private static string VideosAre(int count) => count == 1 ? "1 video is" : $"{count} videos are";
}

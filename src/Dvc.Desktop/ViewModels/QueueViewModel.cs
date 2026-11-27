using System.Collections.ObjectModel;
using System.Diagnostics;
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
    // Shown for any failure that isn't a CompressionFailedException.
    private const string GenericFailureMessage =
        "The video could not be compressed. The file may be corrupt or use an unsupported codec.";

    // Windows and macOS file systems ignore case by default, so the same
    // file can arrive under differently cased paths there.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // How many ffprobe runs may go at once, so adding a hundred videos
    // doesn't start a hundred processes.
    private const int MaxConcurrentProbes = 2;

    private readonly IVideoFilePicker _filePicker;
    private readonly CompressionRunner _compressionRunner;
    private readonly SettingsStore _settingsStore;
    private readonly ILogger<QueueViewModel> _logger;
    private readonly SemaphoreSlim _probeSlots = new(MaxConcurrentProbes);

    // The loop working through the queue, and the video it's compressing.
    // Only touched on the UI thread.
    private Task _processing = Task.CompletedTask;
    private bool _isProcessing;
    private Task _currentCompression = Task.CompletedTask;
    private CancellationTokenSource? _currentCancellation;

    // Set when the current video's compression is cancelled to remove it,
    // rather than to put it back in the queue.
    private bool _removeCurrentWhenStopped;

    public QueueViewModel(
        IVideoFilePicker filePicker, CompressionRunner compressionRunner, SettingsStore settingsStore,
        ILogger<QueueViewModel> logger)
    {
        _filePicker = filePicker;
        _compressionRunner = compressionRunner;
        _settingsStore = settingsStore;
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

    /// <summary>
    /// A one-line description of what the queue is doing and how far through
    /// it is, for the status bar, such as "Compressing clip.mov · 3 of 8
    /// done · 1 failed".
    /// </summary>
    public string Summary
    {
        get
        {
            if (Items.Count == 0)
            {
                return "Add videos to start compressing.";
            }

            var done = Items.Count(item => item.Status == QueueItemStatus.Done);
            var failed = Items.Count(item => item.Status == QueueItemStatus.Failed);
            var counts = $"{done} of {Items.Count} done{(failed > 0 ? $" · {failed} failed" : "")}";

            if (CurrentItem is { } current)
            {
                return $"Compressing {current.FileName} · {counts}{(IsPaused ? " · Pausing after this video" : "")}";
            }

            if (CountWaiting() == 0)
            {
                return $"Queue finished · {counts}";
            }

            return IsPaused ? $"Paused · {counts}" : counts;
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
    /// video already waiting or being compressed. Each added video is probed
    /// in the background, and marked failed straight away if it can't be
    /// read.
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
                var item = new QueueItemViewModel(path);
                Items.Add(item);
                StartProbe(item);
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

        // A video that couldn't be read is probed again, in case the file
        // has been fixed or replaced since.
        if (item.Source is null)
        {
            item.SourceProbe = null;
            StartProbe(item);
        }

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

    partial void OnIsFfmpegReadyChanged(bool value)
    {
        foreach (var item in Items.Where(item => item.Status == QueueItemStatus.Waiting).ToList())
        {
            StartProbe(item);
        }

        StartProcessing();
    }

    // Starts probing a video in the background, unless it's already probed
    // or being probed, or ffprobe hasn't passed the check yet. A video whose
    // probe fails while it's waiting is marked failed then and there; one
    // whose turn has come already fails through its compression instead.
    private void StartProbe(QueueItemViewModel item)
    {
        if (item.SourceProbe is not null || !IsFfmpegReady)
        {
            return;
        }

        var probe = ProbeAsync(item.InputPath);
        item.SourceProbe = probe;
        _ = ShowProbeResultAsync(item, probe);
    }

    private async Task<SourceVideo> ProbeAsync(string inputPath)
    {
        await _probeSlots.WaitAsync();
        try
        {
            return await _compressionRunner.ProbeAsync(inputPath, CancellationToken.None);
        }
        finally
        {
            _probeSlots.Release();
        }
    }

    // Never throws: the outcome is recorded on the item.
    private async Task ShowProbeResultAsync(QueueItemViewModel item, Task<SourceVideo> probe)
    {
        try
        {
            item.Source = await probe;
        }
        catch (Exception ex) when (item.SourceProbe == probe && item.Status == QueueItemStatus.Waiting)
        {
            _logger.LogWarning(ex, "Probing {InputPath} failed", item.InputPath);
            item.Status = QueueItemStatus.Failed;
            item.Details = FailureMessage(ex);
            OnQueueChanged();
        }
        catch
        {
            // The video's compression reports the failure, or it's been
            // removed or probed again since.
        }
    }

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
        item.SizeChange = null;
        item.ClearProgress();
        CurrentItem = item;
        OnQueueChanged();

        // Reports arrive on the thread this was created on (the UI thread in
        // the app). One can still arrive after the compression has ended, so
        // anything from a compression that's no longer current is dropped.
        var elapsed = Stopwatch.StartNew();
        var progress = new Progress<EncodeProgress>(report =>
        {
            if (_currentCancellation == cancellation)
            {
                item.ReportProgress(report, elapsed.Elapsed);
            }
        });

        try
        {
            // Normally probed long before its turn, but a video added just
            // now may still be being probed.
            StartProbe(item);
            var source = await item.SourceProbe!.WaitAsync(cancellation.Token);
            item.Source = source;

            // The settings as they are now, so a change made while videos
            // wait applies to each one that hasn't started yet.
            var video = await _compressionRunner.CompressAsync(
                source, _settingsStore.Current, progress, cancellation.Token);
            item.Status = QueueItemStatus.Done;
            item.SizeChange = DisplayText.SizeChange(source.SizeBytes, video.SizeBytes);
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
            item.Details = FailureMessage(ex);
        }
        finally
        {
            _currentCancellation = null;
            item.ClearProgress();
            CurrentItem = null;
            OnQueueChanged();
        }
    }

    // What to tell the user about a failure. Explained failures carry their
    // own message; anything else mostly carries ffmpeg's raw output, which
    // is logged but not shown.
    private static string FailureMessage(Exception ex) =>
        ex is CompressionFailedException ? ex.Message : GenericFailureMessage;

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

using Dvc.Core.Compression;
using Dvc.Desktop.Services;
using Dvc.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class QueueViewModelTests : IDisposable
{
    private readonly TempFolder _sources = new();
    private readonly TempFolder _output = new();
    private readonly FakeCompressionService _compressionService = new();

    public void Dispose()
    {
        _sources.Dispose();
        _output.Dispose();
    }

    private QueueViewModel CreateQueue(bool ffmpegReady = true, params string[] pickedPaths) => new(
        new FakeFilePicker(pickedPaths),
        new CompressionRunner(_compressionService, NullLogger<CompressionRunner>.Instance, _output.Path),
        NullLogger<QueueViewModel>.Instance)
    {
        IsFfmpegReady = ffmpegReady,
    };

    private static QueueItemViewModel Item(QueueViewModel queue, string path) =>
        queue.Items.Single(item => item.InputPath == path);

    [Fact]
    public async Task AddVideos_CompressesThemOneAfterAnotherInOrder()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mkv");
        var queue = CreateQueue();

        queue.AddVideos([first, second]);
        await queue.WhenIdleAsync();

        Assert.Equal([first, second], _compressionService.EncodedInputs);
        Assert.All(queue.Items, item => Assert.Equal(QueueItemStatus.Done, item.Status));
        Assert.Equal($"Saved to {Path.Combine(_output.Path, "first.mp4")}", Item(queue, first).Details);
        Assert.Equal(["first.mp4", "second.mp4"], _output.FileNames());
        Assert.Equal("Queue finished.", queue.Summary);
        Assert.Null(queue.CurrentItem);
    }

    [Fact]
    public async Task AddVideosCommand_AddsWhatThePickerReturns()
    {
        var clip = _sources.File("clip.mov");
        var queue = CreateQueue(ffmpegReady: true, clip);

        await queue.AddVideosCommand.ExecuteAsync(null);
        await queue.WhenIdleAsync();

        Assert.Equal(QueueItemStatus.Done, Item(queue, clip).Status);
    }

    [Fact]
    public async Task AddVideos_BeforeFfmpegIsReady_WaitsUntilItIs()
    {
        var clip = _sources.File("clip.mov");
        var queue = CreateQueue(ffmpegReady: false);

        queue.AddVideos([clip]);

        Assert.Equal(QueueItemStatus.Waiting, Item(queue, clip).Status);
        Assert.Empty(_compressionService.EncodedInputs);

        queue.IsFfmpegReady = true;
        await queue.WhenIdleAsync();

        Assert.Equal(QueueItemStatus.Done, Item(queue, clip).Status);
    }

    [Fact]
    public void AddVideos_SkipsFoldersAndUnsupportedFiles()
    {
        var notes = _sources.File("notes.txt");
        var folder = Directory.CreateDirectory(Path.Combine(_sources.Path, "Holiday")).FullName;
        var queue = CreateQueue();

        queue.AddVideos([notes, folder]);

        Assert.Empty(queue.Items);
        Assert.Equal(
            "Skipped 2 files: notes.txt (not a supported video type), Holiday (a folder).", queue.Notice);
    }

    [Fact]
    public async Task AddVideos_SkipsAVideoAlreadyWaitingOrBeingCompressed()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mov");
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([first, second]);
        queue.AddVideos([first.ToUpperInvariant(), second]);

        Assert.Equal(2, queue.Items.Count);
        Assert.Equal(
            "Skipped 2 files: FIRST.MOV (already in the queue), second.mov (already in the queue).", queue.Notice);

        await queue.StopAsync();
    }

    [Fact]
    public async Task AddVideos_TakesAVideoThatAlreadyFinished()
    {
        var clip = _sources.File("clip.mov");
        var queue = CreateQueue();

        queue.AddVideos([clip]);
        await queue.WhenIdleAsync();
        queue.AddVideos([clip]);
        await queue.WhenIdleAsync();

        Assert.Equal(2, queue.Items.Count);
        Assert.Null(queue.Notice);
        Assert.Equal(["clip (1).mp4", "clip.mp4"], _output.FileNames());
    }

    [Fact]
    public async Task Pause_LetsTheCurrentVideoFinishThenStartsNoOther()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mov");
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([first, second]);
        queue.PauseCommand.Execute(null);
        Assert.Equal("Compressing first.mov · 1 waiting · Pausing after this video", queue.Summary);

        _compressionService.ReleaseEncode();
        await queue.WhenIdleAsync();

        Assert.Equal(QueueItemStatus.Done, Item(queue, first).Status);
        Assert.Equal(QueueItemStatus.Waiting, Item(queue, second).Status);
        Assert.Equal([first], _compressionService.EncodedInputs);
        Assert.Equal("Paused · 1 waiting", queue.Summary);
    }

    [Fact]
    public async Task Resume_CarriesOnWithTheNextWaitingVideo()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mov");
        var queue = CreateQueue();
        queue.PauseCommand.Execute(null);

        queue.AddVideos([first, second]);
        Assert.Empty(_compressionService.EncodedInputs);

        queue.ResumeCommand.Execute(null);
        await queue.WhenIdleAsync();

        Assert.False(queue.IsPaused);
        Assert.Equal([first, second], _compressionService.EncodedInputs);
    }

    [Fact]
    public async Task StopNow_PutsTheCurrentVideoBackFirstAndPauses()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mov");
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([first, second]);
        Assert.True(queue.StopNowCommand.CanExecute(null));
        await queue.StopNowCommand.ExecuteAsync(null);

        Assert.True(queue.IsPaused);
        Assert.Null(queue.CurrentItem);
        Assert.Equal([first, second], queue.Items.Select(item => item.InputPath));
        Assert.All(queue.Items, item => Assert.Equal(QueueItemStatus.Waiting, item.Status));
        Assert.Empty(_output.FileNames());

        _compressionService.HoldEncodes = false;
        queue.ResumeCommand.Execute(null);
        await queue.WhenIdleAsync();

        Assert.Equal([first, first, second], _compressionService.EncodedInputs);
        Assert.All(queue.Items, item => Assert.Equal(QueueItemStatus.Done, item.Status));
    }

    [Fact]
    public void StopNow_WithNothingCompressing_IsDisabled()
    {
        var queue = CreateQueue();

        Assert.False(queue.StopNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Remove_OnTheCurrentVideo_StopsItAndMovesOn()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mov");
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([first, second]);
        var current = Item(queue, first);
        var next = Item(queue, second);
        queue.RemoveCommand.Execute(current);

        await Wait.UntilAsync(() => next.Status == QueueItemStatus.Compressing);
        Assert.Equal([next], queue.Items);
        Assert.False(queue.IsPaused);

        _compressionService.ReleaseEncode();
        await Wait.UntilAsync(() => next.Status == QueueItemStatus.Done);
        await queue.WhenIdleAsync();
        Assert.Equal(["second.mp4"], _output.FileNames());
    }

    [Fact]
    public async Task Remove_OnAWaitingVideo_LeavesTheCurrentOneRunning()
    {
        var first = _sources.File("first.mov");
        var second = _sources.File("second.mov");
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([first, second]);
        queue.RemoveCommand.Execute(Item(queue, second));

        Assert.Equal([first], queue.Items.Select(item => item.InputPath));
        Assert.Equal(QueueItemStatus.Compressing, Item(queue, first).Status);

        _compressionService.ReleaseEncode();
        await queue.WhenIdleAsync();
        Assert.Equal([first], _compressionService.EncodedInputs);
    }

    [Fact]
    public async Task AnExplainedFailure_ShowsItsMessageAndTheQueueCarriesOn()
    {
        var broken = _sources.File("broken.mov");
        var fine = _sources.File("fine.mov");
        var queue = CreateQueue();
        _compressionService.FailingInputs.Add(broken);

        queue.AddVideos([broken, fine]);
        await queue.WhenIdleAsync();

        Assert.Equal(QueueItemStatus.Failed, Item(queue, broken).Status);
        Assert.Equal("The file has no video stream.", Item(queue, broken).Details);
        Assert.Equal(QueueItemStatus.Done, Item(queue, fine).Status);
    }

    [Fact]
    public async Task AnyOtherFailure_ShowsAGenericMessage()
    {
        var clip = _sources.File("clip.mov");
        var queue = CreateQueue();
        _compressionService.EncodeFailure = new InvalidOperationException("ffmpeg version 8.0 Copyright (c) ... raw output");

        queue.AddVideos([clip]);
        await queue.WhenIdleAsync();

        Assert.StartsWith("The video could not be compressed.", Item(queue, clip).Details);
        Assert.DoesNotContain("raw output", Item(queue, clip).Details);
    }

    [Fact]
    public async Task Retry_PutsAFailedVideoAtTheEndAndCompressesItAgain()
    {
        var broken = _sources.File("broken.mov");
        var fine = _sources.File("fine.mov");
        var queue = CreateQueue();
        _compressionService.FailingInputs.Add(broken);

        queue.AddVideos([broken, fine]);
        await queue.WhenIdleAsync();
        _compressionService.FailingInputs.Clear();
        queue.RetryCommand.Execute(Item(queue, broken));
        await queue.WhenIdleAsync();

        Assert.Equal([fine, broken], queue.Items.Select(item => item.InputPath));
        Assert.Equal(QueueItemStatus.Done, Item(queue, broken).Status);
    }

    [Fact]
    public async Task ClearFinished_RemovesDoneAndFailedVideosOnly()
    {
        var broken = _sources.File("broken.mov");
        var fine = _sources.File("fine.mov");
        var later = _sources.File("later.mov");
        var queue = CreateQueue();
        _compressionService.FailingInputs.Add(broken);

        queue.AddVideos([broken, fine]);
        await queue.WhenIdleAsync();
        queue.PauseCommand.Execute(null);
        queue.AddVideos([later]);
        queue.ClearFinishedCommand.Execute(null);

        Assert.Equal([later], queue.Items.Select(item => item.InputPath));
        Assert.Equal(["fine.mp4"], _output.FileNames());
        Assert.False(queue.ClearFinishedCommand.CanExecute(null));
    }

    [Fact]
    public async Task CloseWarning_IsNullWithNothingLeftToDo()
    {
        var queue = CreateQueue();
        Assert.Null(queue.CloseWarning);

        queue.AddVideos([_sources.File("clip.mov")]);
        await queue.WhenIdleAsync();
        Assert.Null(queue.CloseWarning);
    }

    [Fact]
    public void CloseWarning_WithVideosWaiting_SaysHowMany()
    {
        var queue = CreateQueue();
        queue.PauseCommand.Execute(null);

        queue.AddVideos([_sources.File("first.mov"), _sources.File("second.mov")]);

        Assert.Equal("2 videos are still waiting. Closing the app empties the queue.", queue.CloseWarning);
    }

    [Fact]
    public async Task CloseWarning_WhileCompressing_NamesTheVideo()
    {
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([_sources.File("first.mov"), _sources.File("second.mov")]);

        Assert.Equal(
            "first.mov is still being compressed, and 1 video is waiting. " +
            "Closing the app stops the compression and empties the queue.",
            queue.CloseWarning);

        await queue.StopAsync();
    }

    [Fact]
    public async Task StopAsync_CompletesOnceTheUnfinishedOutputIsGone()
    {
        var clip = _sources.File("clip.mov");
        var queue = CreateQueue();
        _compressionService.HoldEncodes = true;

        queue.AddVideos([clip]);
        Assert.Single(_output.FileNames());
        await queue.StopAsync();

        Assert.Empty(_output.FileNames());
        Assert.False(queue.IsCompressing);
        Assert.Equal(QueueItemStatus.Waiting, Item(queue, clip).Status);
    }
}

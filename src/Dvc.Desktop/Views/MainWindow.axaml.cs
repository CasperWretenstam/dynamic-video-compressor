using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Dvc.Desktop.ViewModels;

namespace Dvc.Desktop.Views;

public partial class MainWindow : Window
{
    // Set once the user has agreed to close despite unfinished work, so the
    // close that follows isn't asked about again.
    private bool _closeConfirmed;

    // Set while the close confirmation is showing, so a second close
    // request doesn't open another.
    private bool _confirmingClose;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // Only files can be dropped. Whether each one is a video the queue
    // takes is decided when it's added.
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { IsFfmpegReady: true } viewModel || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        // An item from a remote or virtual location has no local path for
        // ffmpeg to read.
        viewModel.Queue.AddVideos(items.Select(item => item.TryGetLocalPath()).OfType<string>());
    }

    // Closing with videos waiting or one being compressed asks first. If the
    // user agrees, the compression is stopped, which kills ffmpeg and
    // deletes the unfinished output, before the window closes; otherwise
    // ffmpeg would keep running after the app exits.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _closeConfirmed || ViewModel?.Queue is not { } queue)
        {
            return;
        }

        if (_confirmingClose)
        {
            e.Cancel = true;
            return;
        }

        if (queue.CloseWarning is not { } warning)
        {
            return;
        }

        e.Cancel = true;
        _confirmingClose = true;
        try
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                this, "Close Dynamic Video Compressor?", warning, confirmText: "Close anyway", cancelText: "Keep open");
            if (!confirmed)
            {
                return;
            }

            await queue.StopAsync();
            _closeConfirmed = true;
        }
        finally
        {
            _confirmingClose = false;
        }

        Close();
    }
}

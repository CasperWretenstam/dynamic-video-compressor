using Avalonia.Controls;
using Dvc.Desktop.ViewModels;

namespace Dvc.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Closing mid-encode would leave ffmpeg running after the app exits and
    // a partial file behind. So the close is held off until the compression
    // is cancelled and cleaned up, then retried.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || DataContext is not MainWindowViewModel { IsCompressing: true } viewModel)
        {
            return;
        }

        e.Cancel = true;
        await viewModel.CancelCompressionAsync();
        Close();
    }
}

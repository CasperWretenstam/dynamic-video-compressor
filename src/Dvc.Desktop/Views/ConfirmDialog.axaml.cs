using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Dvc.Desktop.Views;

// A yes/no question in a small modal window, since Avalonia's MIT-licensed
// packages have no message box. The safe choice (cancel) is the
// highlighted button and is what Escape and closing the dialog pick.
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    // Shows the question over the owner and completes with true if the user
    // confirmed.
    public static async Task<bool> ShowAsync(
        Window owner, string title, string message, string confirmText, string cancelText)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;

        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

using Avalonia;

namespace Dvc.Desktop;

internal sealed class Program
{
    // Nothing that needs Avalonia or a UI SynchronizationContext may run
    // before StartWithClassicDesktopLifetime: neither exists yet.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Also called by IDE XAML previewers, so it must stay public, static and
    // free of side effects.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

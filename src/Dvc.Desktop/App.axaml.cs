using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dvc.Core.Compression;
using Dvc.Desktop.Services;
using Dvc.Desktop.ViewModels;
using Dvc.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dvc.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // XAML previewers run this too, without a desktop lifetime; they get
        // no window and no host.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = BuildHost();
            desktop.Exit += (_, _) => host.Dispose();

            var window = host.Services.GetRequiredService<MainWindow>();
            var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            window.DataContext = viewModel;
            desktop.MainWindow = window;

            // The check reports every failure as a status message rather than
            // throwing, so nothing is lost by not awaiting it.
            _ = viewModel.CheckFfmpegAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // A windowed app has no console, so its logs go to the debugger's
        // output instead.
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        builder.Services.AddSingleton<IVideoCompressionService, VideoCompressionService>();
        builder.Services.AddSingleton<FfmpegCheck>();
        builder.Services.AddSingleton<CompressionRunner>();
        builder.Services.AddSingleton<MainWindow>();
        // The file picker opens over the main window.
        builder.Services.AddSingleton<IVideoFilePicker>(services =>
            new VideoFilePicker(services.GetRequiredService<MainWindow>()));
        builder.Services.AddSingleton<QueueViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();

        return builder.Build();
    }
}

# Dvc.Desktop

The desktop app. See the root `CLAUDE.md` for the repo as a whole (commands, git workflow, shared conventions, the FFmpeg dependency) and `src/Dvc.Core/CLAUDE.md` for the compression engine this app calls in-process. Paths below are relative to `src/Dvc.Desktop/`.

## What this is

A desktop app that compresses videos on the user's own machine with `Dvc.Core`. There's no server, job queue or database: the app builds a `CompressionRequest` and calls Core's `IVideoCompressionService` directly. Windows is the first target. The stack runs on macOS and Linux too, but nothing has been tested there yet.

## Commands

Run the app: `dotnet run --project src/Dvc.Desktop`

## Stack

- **Avalonia 12** with the Fluent theme. Only the MIT-licensed framework packages are used. The template's `AvaloniaUI.DiagnosticsSupport` package and its `WithDeveloperTools()` call were left out: they belong to Avalonia's separately licensed tools, whose free tier sends telemetry. The template's Inter font package and Avalonia logo icon were left out too, so the app uses the system font and the default window icon.
- **CommunityToolkit.Mvvm** for view models: `[ObservableProperty]` on partial properties, and `[RelayCommand]` for commands.
- **Microsoft.Extensions.Hosting** for dependency injection and logging. Logging goes to the debugger's output only (`AddDebug`), since a windowed app has no console.

The template's reflection-based `ViewLocator` was left out (`-rvl`). `App` creates the main window itself and sets its view model as the `DataContext`, so nothing needs to map view models to views by name.

## Structure

- `Program.cs`: the Avalonia entry point. `BuildAvaloniaApp` stays public, static and free of side effects, because IDE XAML previewers call it.
- `App.axaml.cs`: builds the host in `OnFrameworkInitializationCompleted`, creates `MainWindow` with `MainWindowViewModel` from the container, and disposes the host when the app exits. A XAML previewer runs this without a desktop lifetime, so the host and window are only created under `IClassicDesktopStyleApplicationLifetime`.
- `Services/FfmpegCheck.cs`: the startup check (see below).
- `ViewModels/MainWindowViewModel.cs`: the main window's state, currently the status message and whether ffmpeg is ready.
- `Views/MainWindow.axaml`: the main window.

Registered services: `IVideoCompressionService` → `VideoCompressionService` (from Core), `FfmpegCheck` and `MainWindowViewModel`, all singletons since there's one window.

## ffmpeg check

Like the API, the app needs `ffmpeg` and `ffprobe` on PATH, and ffmpeg needs libx264. A desktop user may have neither, and an encode without them fails with an exception that doesn't say why. So at startup, `App` starts `MainWindowViewModel.CheckFfmpegAsync`, which runs `FfmpegCheck.CheckAsync`:

- `ffmpeg -hide_banner -encoders` must start, exit 0, and list `libx264`. `FfmpegCheck.HasEncoder` matches the encoder's name exactly, so a build with only `libx264rgb` doesn't pass.
- `ffprobe -version` must start and exit 0.
- Each run has a 15-second timeout, and the process is killed if it's still running after that.

Problems come back as sentences in `FfmpegCheckResult.Problems` rather than as exceptions. The view model shows them in the window with install instructions (gyan.dev's essentials build, via `winget install Gyan.FFmpeg.Essentials`). The bare names `ffmpeg`/`ffprobe` are looked up on PATH, the same way FFMpegCore finds them. Tests pass other names to simulate missing executables.

## Comments

The root `CLAUDE.md`'s comment rules apply. `///` goes on the public members of `Services/` and `ViewModels/`. Views and `App` use ordinary `//` comments.

## Tests

`tests/Dvc.Desktop.Tests` covers `FfmpegCheck.HasEncoder`'s parsing, `FfmpegCheck.CheckAsync` and `MainWindowViewModel.CheckFfmpegAsync` with missing executables (no ffmpeg needed). The working-ffmpeg path depends on the machine, so it isn't tested.

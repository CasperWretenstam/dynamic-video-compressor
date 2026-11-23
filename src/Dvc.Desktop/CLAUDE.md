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
- `Services/FixedCompressionSettings.cs`: the settings every compression uses, in one place (see "Compressing a video").
- `Services/CompressionRunner.cs`: compresses one video end to end: probe, plan, encode to a temporary file, move it to its final name.
- `Services/OutputNaming.cs`: output and temporary file names, and the never-overwrite move.
- `Services/VideoFilePicker.cs`: `IVideoFilePicker` and its Avalonia implementation, the system's single-file picker. The view model only sees the interface, so tests swap in a fake.
- `ViewModels/MainWindowViewModel.cs`: the main window's state: the status message, whether ffmpeg is ready, whether a compression is running, and the **Choose video** command.
- `Views/MainWindow.axaml`: the main window, a button and the status line. Its code-behind holds off closing while a compression runs (see below).

Registered services: `IVideoCompressionService` → `VideoCompressionService` (from Core), `FfmpegCheck`, `CompressionRunner`, `MainWindow`, `IVideoFilePicker` → `VideoFilePicker` (opening over `MainWindow`) and `MainWindowViewModel`, all singletons since there's one window. `MainWindow` is in the container only so the picker can get it; `App` sets its `DataContext`.

## ffmpeg check

Like the API, the app needs `ffmpeg` and `ffprobe` on PATH, and ffmpeg needs libx264. A desktop user may have neither, and an encode without them fails with an exception that doesn't say why. So at startup, `App` starts `MainWindowViewModel.CheckFfmpegAsync`, which runs `FfmpegCheck.CheckAsync`:

- `ffmpeg -hide_banner -encoders` must start, exit 0, and list `libx264`. `FfmpegCheck.HasEncoder` matches the encoder's name exactly, so a build with only `libx264rgb` doesn't pass.
- `ffprobe -version` must start and exit 0.
- Each run has a 15-second timeout, and the process is killed if it's still running after that.

Problems come back as sentences in `FfmpegCheckResult.Problems` rather than as exceptions. The view model shows them in the window with install instructions (gyan.dev's essentials build, via `winget install Gyan.FFmpeg.Essentials`). The bare names `ffmpeg`/`ffprobe` are looked up on PATH, the same way FFMpegCore finds them. Tests pass other names to simulate missing executables.

## Compressing a video

The **Choose video** button runs `MainWindowViewModel.ChooseVideoCommand`, which can only run once ffmpeg passed the check and while no compression is running (`IsCompressing`), so there's one compression at a time. The picker filters by `VideoFileDefinitions.SupportedExtensions`; picking a file starts the compression, and cancelling the picker does nothing.

`CompressionRunner.CompressAsync` then:

1. Re-checks the extension, since a name can be typed into the picker, failing with a `CompressionFailedException`.
2. Builds the request from `FixedCompressionSettings`, probes the source and plans the encode with Core's `CompressionPlanner`, just like the API's worker.
3. Creates the output folder if needed and encodes to a temporary file there, `<name>.<guid>.partial.mp4`. It keeps `.mp4` because ffmpeg picks the container from the extension.
4. On success, `OutputNaming.MoveToFreeName` moves it to `<name>.mp4`, or the first free `<name> (n).mp4`. It skips any existing file or folder, and moves with `overwrite: false`, so a file created between the check and the move is skipped too rather than replaced. That also covers a source that sits in the output folder already. The final name is chosen after the encode, not before, so nothing reserves a name during a long encode.
5. On any failure or cancellation, deletes the temporary file and rethrows.

**Fixed settings.** `FixedCompressionSettings` matches the API's shipped defaults: `Balanced`, short side capped at 1080 and fps at 30 (both following the source below that), preset `slow`. `TwoPass` is set but unused, since it only affects target-size encodes. A settings UI would replace this class.

**Output folder.** `CompressionRunner.DefaultOutputFolder` is `Dynamic Video Compressor` under `Environment.SpecialFolder.MyVideos`, falling back to `~/Videos` where that's empty (a Linux desktop without XDG user directories). Tests pass their own folder to the constructor.

**Status and errors.** The view model shows "Compressing…", then "Done. Saved to <path>" (plus any warnings) or "Couldn't compress <file>. <reason>". The reason follows the API worker's rule: a `CompressionFailedException`'s message as-is, anything else a generic message, with the full exception logged either way.

**Closing mid-encode.** Without help, ffmpeg would keep running after the app exits and leave the partial file behind. `MainWindow.OnClosing` cancels the close if a compression is running, awaits `MainWindowViewModel.CancelCompressionAsync` (which cancels the token and waits for the compression's cleanup), then closes again. Cancelling makes FFMpegCore kill ffmpeg and throw `OperationCanceledException` straight away, before the process has fully exited (measured: it's gone about 60-80ms later). So the temporary file's delete retries for up to five seconds in case ffmpeg still holds it. Any exception after a cancel counts as the cancel, as in the API worker. If the app itself is killed, nothing can stop ffmpeg; the README tells users the `.partial.mp4` is safe to delete.

## Comments

The root `CLAUDE.md`'s comment rules apply. `///` goes on the public members of `Services/` and `ViewModels/`. Views and `App` use ordinary `//` comments.

## Tests

`tests/Dvc.Desktop.Tests` needs no ffmpeg. It covers:

- `FfmpegCheck.HasEncoder`'s parsing, and `FfmpegCheck.CheckAsync` and `MainWindowViewModel.CheckFfmpegAsync` with missing executables. The working-ffmpeg path depends on the machine, so it isn't tested.
- `OutputNaming`: the extension change, collision numbering, and never overwriting, in a temporary folder.
- `CompressionRunner` and the view model's compression flow, with a fake `IVideoCompressionService` (`TestSupport.cs`) that writes a partial file and then succeeds, throws, or waits to be cancelled: naming, cleanup on failure and cancellation, the status messages, and the command being disabled while a compression runs.

The real encode, the file picker and closing the window mid-encode aren't covered by tests; check them by hand.

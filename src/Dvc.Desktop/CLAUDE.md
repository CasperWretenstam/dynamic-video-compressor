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
- `Services/FixedCompressionSettings.cs`: the settings every compression uses, in one place (see "The queue").
- `Services/CompressionRunner.cs`: compresses one video end to end: probe, plan, encode to a temporary file, move it to its final name.
- `Services/OutputNaming.cs`: output and temporary file names, and the never-overwrite move.
- `Services/VideoFilePicker.cs`: `IVideoFilePicker` and its Avalonia implementation, the system's multi-file picker. The view model only sees the interface, so tests swap in a fake.
- `ViewModels/MainWindowViewModel.cs`: the main window's state: whether ffmpeg is ready, the message shown until it is, and the queue (`Queue`). It passes ffmpeg's readiness on to the queue.
- `ViewModels/QueueViewModel.cs`: the compression queue and its commands (see "The queue").
- `ViewModels/QueueItemViewModel.cs`: one video in the queue: its path, `QueueItemStatus` and the outcome text.
- `Views/MainWindow.axaml`: the window: the toolbar, the ffmpeg and skipped-files messages, the queue and the status bar. Its code-behind handles dropped files and asks before closing with work left (see "Closing with work left").
- `Views/QueueView.axaml`: the queue list, or the drop area while it's empty. Its data context is the `QueueViewModel`.
- `Views/ConfirmDialog.axaml`: a small modal yes/no window, since Avalonia's MIT packages have no message box.
- `Styles/Resources.axaml` and `Styles/AppStyles.axaml`: see "Layout and styles".

Registered services: `IVideoCompressionService` → `VideoCompressionService` (from Core), `FfmpegCheck`, `CompressionRunner`, `MainWindow`, `IVideoFilePicker` → `VideoFilePicker` (opening over `MainWindow`), `QueueViewModel` and `MainWindowViewModel`, all singletons since there's one window. `MainWindow` is in the container only so the picker can get it; `App` sets its `DataContext`.

## Layout and styles

One window: a toolbar along the top, the queue filling the middle, and a status bar along the bottom (`QueueViewModel.Summary`). A settings panel will sit to the right of the queue. Each region gets its own view and view model as it grows, so it can be moved without rewriting its logic; the toolbar is still part of `MainWindow.axaml`, bound to the queue's commands.

Views don't set colours, spacing or fonts themselves. They name what an element is with a style class (`Border.bar`, `Border.card`, `Border.warning`, `Border.dropZone`, `TextBlock.title`/`secondary`/`success`/`danger`), and `Styles/AppStyles.axaml` decides how that looks. The values those styles use live in `Styles/Resources.axaml`: the app's colours, in a light and a dark version (`ThemeDictionaries`, picked to match the system theme, since `App.axaml` requests the `Default` variant), and spacing (`PagePadding`, `ControlSpacing` and so on). Restyling the app means changing those two files.

Text is English only and written directly in the views and view models; there are no resource strings.

## ffmpeg check

Like the API, the app needs `ffmpeg` and `ffprobe` on PATH, and ffmpeg needs libx264. A desktop user may have neither, and an encode without them fails with an exception that doesn't say why. So at startup, `App` starts `MainWindowViewModel.CheckFfmpegAsync`, which runs `FfmpegCheck.CheckAsync`:

- `ffmpeg -hide_banner -encoders` must start, exit 0, and list `libx264`. `FfmpegCheck.HasEncoder` matches the encoder's name exactly, so a build with only `libx264rgb` doesn't pass.
- `ffprobe -version` must start and exit 0.
- Each run has a 15-second timeout, and the process is killed if it's still running after that.

Problems come back as sentences in `FfmpegCheckResult.Problems` rather than as exceptions. The view model shows them in the window with install instructions (gyan.dev's essentials build, via `winget install Gyan.FFmpeg.Essentials`). The bare names `ffmpeg`/`ffprobe` are looked up on PATH, the same way FFMpegCore finds them. Tests pass other names to simulate missing executables.

## The queue

`QueueViewModel` holds the videos in an `ObservableCollection` in memory; nothing is saved, so the queue is gone when the app closes. Videos come from **Add videos** (`AddVideosCommand`, the picker, filtered by `VideoFileDefinitions.SupportedExtensions`) or from files dropped on the window (`MainWindow.OnDrop`), and both go through `AddVideos`. It skips folders, unsupported extensions and a path already waiting or being compressed (compared ignoring case except on Linux), naming each in `Notice`. A path that's done or failed can be added again. Adding is only enabled, and dropping only accepted, once ffmpeg passed the check.

A video is `Waiting`, `Compressing`, `Done` or `Failed`. There's no cancelled state: a stopped video goes back to waiting, or is removed.

- **Processing.** `StartProcessing` starts `ProcessAsync` unless it's already running, the queue is paused or ffmpeg isn't ready. It's called on adding, resuming, retrying and ffmpeg becoming ready. `ProcessAsync` compresses the first waiting video, one at a time, until none are left or the queue is paused. The pause is checked between videos, so the current one always finishes. `WhenIdleAsync` returns that loop's task.
- **Pause / Resume.** `IsPaused` only stops the next video from starting. Pausing an idle queue keeps added videos waiting.
- **Stop now** (`StopAsync`) pauses, cancels the current compression and waits for its cleanup. The video goes back to `Waiting` in its place, which is ahead of every other waiting video, so it's first when the queue resumes, starting from scratch.
- **Remove** on a waiting or finished video takes it out of the list. On the current video it cancels the compression with `_removeCurrentWhenStopped` set, so the cancellation removes the item instead of putting it back, and the loop moves on unless paused.
- **Retry** moves a failed video to the end as waiting. **Clear finished** removes done and failed videos.

Each compression runs through `CompressionRunner.CompressAsync`, which:

1. Re-checks the extension, since a name can be typed into the picker, failing with a `CompressionFailedException`.
2. Builds the request from `FixedCompressionSettings`, probes the source and plans the encode with Core's `CompressionPlanner`, just like the API's worker.
3. Creates the output folder if needed and encodes to a temporary file there, `<name>.<guid>.partial.mp4`. It keeps `.mp4` because ffmpeg picks the container from the extension.
4. On success, `OutputNaming.MoveToFreeName` moves it to `<name>.mp4`, or the first free `<name> (n).mp4`. It skips any existing file or folder, and moves with `overwrite: false`, so a file created between the check and the move is skipped too rather than replaced. That also covers a source that sits in the output folder already. The final name is chosen after the encode, not before, so nothing reserves a name during a long encode.
5. On any failure or cancellation, deletes the temporary file and rethrows.

**Fixed settings.** `FixedCompressionSettings` matches the API's shipped defaults: `Balanced`, short side capped at 1080 and fps at 30 (both following the source below that), preset `slow`. `TwoPass` is set but unused, since it only affects target-size encodes. A settings UI would replace this class.

**Output folder.** `CompressionRunner.DefaultOutputFolder` is `Dynamic Video Compressor` under `Environment.SpecialFolder.MyVideos`, falling back to `~/Videos` where that's empty (a Linux desktop without XDG user directories). Tests pass their own folder to the constructor.

**Outcome text.** A done video's `QueueItemViewModel.Details` is "Saved to <path>" plus any warnings; a failed one's is the reason. The reason follows the API worker's rule: a `CompressionFailedException`'s message as-is, anything else a generic message, with the full exception logged either way.

## Closing with work left

Without help, ffmpeg would keep running after the app exits and leave the partial file behind, and the queue would be lost without warning. If `QueueViewModel.CloseWarning` isn't null (a video is waiting or being compressed), `MainWindow.OnClosing` cancels the close and shows `ConfirmDialog` with that warning. **Keep open** is the highlighted button and what Escape picks. On **Close anyway** it awaits `QueueViewModel.StopAsync` (which cancels the compression and waits for its cleanup), then closes again. A second close request while the dialog is up is ignored. Closing with only finished videos doesn't ask.

Cancelling makes FFMpegCore kill ffmpeg and throw `OperationCanceledException` straight away, before the process has fully exited (measured: it's gone about 60-80ms later). So the temporary file's delete retries for up to five seconds in case ffmpeg still holds it. Any exception after a cancel counts as the cancel, as in the API worker. If the app itself is killed, nothing can stop ffmpeg; the README tells users the `.partial.mp4` is safe to delete.

## Comments

The root `CLAUDE.md`'s comment rules apply. `///` goes on the public members of `Services/` and `ViewModels/`. Views and `App` use ordinary `//` comments.

## Tests

`tests/Dvc.Desktop.Tests` needs no ffmpeg. It covers:

- `FfmpegCheck.HasEncoder`'s parsing, and `FfmpegCheck.CheckAsync` and `MainWindowViewModel.CheckFfmpegAsync` with missing executables. The working-ffmpeg path depends on the machine, so it isn't tested.
- `OutputNaming`: the extension change, collision numbering, and never overwriting, in a temporary folder.
- `CompressionRunner` and `QueueViewModel`, with a fake `IVideoCompressionService` (`TestSupport.cs`) that writes a partial file and then succeeds, throws, or holds until released or cancelled: naming, cleanup on failure and cancellation, processing order, pause and resume, stop now, remove, retry, clear finished, skipped files, failure messages and the close warning. Everything up to a held encode runs synchronously, so most tests need no waiting; tests that release or cancel a held encode poll with `Wait.UntilAsync`.

The real encode, the file picker, drag and drop, the views and the close confirmation aren't covered by tests; check them by hand.

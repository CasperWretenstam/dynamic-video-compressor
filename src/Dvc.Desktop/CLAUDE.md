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
- `Services/CompressionSettings.cs`: the user's settings as a record, with their defaults, the choices on offer, and the `CompressionRequest` they build (see "Settings").
- `Services/SettingsStore.cs`: the settings in effect, loaded from and saved to the settings file (see "Settings").
- `Services/CompressionRunner.cs`: probes a video (`ProbeAsync`, giving a `SourceVideo`), then compresses it (`CompressAsync`): plan, encode to a temporary file, move it to its final name.
- `Services/OutputNaming.cs`: output and temporary file names, and the never-overwrite move.
- `Services/VideoFilePicker.cs`: `IVideoFilePicker` and its Avalonia implementation, the system's multi-file picker. The view model only sees the interface, so tests swap in a fake.
- `ViewModels/MainWindowViewModel.cs`: the main window's state: whether ffmpeg is ready, the message shown until it is, the queue (`Queue`), the settings panel (`Settings`) and whether it's open. It passes ffmpeg's readiness on to the queue.
- `ViewModels/SettingsViewModel.cs`: the settings panel's choices (`SettingOption<T>` lists), the ones picked, saving on change, and Reset to defaults.
- `ViewModels/QueueViewModel.cs`: the compression queue and its commands (see "The queue").
- `ViewModels/QueueItemViewModel.cs`: one video in the queue: its path, `QueueItemStatus`, probed details, progress and the outcome text.
- `ViewModels/DisplayText.cs`: sizes, durations, size changes and time remaining written out for the user.
- `Views/MainWindow.axaml`: the window: the toolbar, the ffmpeg and skipped-files messages, the queue and the status bar. Its code-behind handles dropped files and asks before closing with work left (see "Closing with work left").
- `Views/QueueView.axaml`: the queue list, or the drop area while it's empty. Its data context is the `QueueViewModel`.
- `Views/SettingsView.axaml`: the settings panel. Its data context is the `SettingsViewModel`.
- `Views/ConfirmDialog.axaml`: a small modal yes/no window, since Avalonia's MIT packages have no message box.
- `Styles/Resources.axaml` and `Styles/AppStyles.axaml`: see "Layout and styles".

Registered services: `IVideoCompressionService` → `VideoCompressionService` (from Core), `FfmpegCheck`, `CompressionRunner`, `SettingsStore`, `MainWindow`, `IVideoFilePicker` → `VideoFilePicker` (opening over `MainWindow`), `QueueViewModel`, `SettingsViewModel` and `MainWindowViewModel`, all singletons since there's one window. `MainWindow` is in the container only so the picker can get it; `App` sets its `DataContext`.

## Layout and styles

One window: a toolbar along the top, the queue filling the middle, the settings panel (`SettingsView`, 300 wide) to its right, and a status bar along the bottom (`QueueViewModel.Summary`). The **Settings** toggle at the toolbar's right end shows or hides the panel (`MainWindowViewModel.IsSettingsOpen`, open at start and not remembered). Each region gets its own view and view model as it grows, so it can be moved without rewriting its logic; the toolbar is still part of `MainWindow.axaml`, bound to the queue's commands.

Views don't set colours, spacing or fonts themselves. They name what an element is with a style class (`Border.bar`, `Border.panel`, `Border.card`, `Border.warning`, `Border.dropZone`, `TextBlock.title`/`secondary`/`success`/`danger`), and `Styles/AppStyles.axaml` decides how that looks. The values those styles use live in `Styles/Resources.axaml`: the app's colours, in a light and a dark version (`ThemeDictionaries`, picked to match the system theme, since `App.axaml` requests the `Default` variant), and spacing (`PagePadding`, `ControlSpacing` and so on). Restyling the app means changing those two files.

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
- **Retry** moves a failed video to the end as waiting, probing it again if the probe was what failed. **Clear finished** removes done and failed videos.
- **Status bar.** `Summary` counts done and failed videos against the whole list ("Compressing clip.mov · 3 of 8 done · 1 failed").

**Probing on add.** Each added video is probed straight away in the background (`StartProbe`), at most two ffprobe runs at a time, so its resolution, duration, framerate and size (`QueueItemViewModel.SourceDetails`) show long before its turn. The probe's task is kept on the item (`QueueItemViewModel.SourceProbe`), and its compression awaits that same task rather than probing again; a video whose turn comes first just waits for it, and Stop now doesn't wait for it to finish. A probe that fails while the video is waiting marks it failed then and there; one that fails once the video's turn has come fails through its compression instead, so the two never both set the status. Probes only start once ffmpeg passed the check; becoming ready probes the waiting videos.

**Progress.** `CompressAsync` hands the runner a `Progress<EncodeProgress>`, created on the UI thread so reports arrive there. A report that arrives after its compression ended (checked by comparing the compression's cancellation source with the current one) is dropped. `QueueItemViewModel.ReportProgress` turns a report into the bar's value, `IsProgressKnown` (false gives a bar with no fixed end, for a source of unknown duration), the stage (`Phase`: "First pass", "Second pass", or "Correcting size" for a redone attempt) and the time remaining. The time remaining extrapolates from the elapsed time and fraction of the current attempt, measured from that attempt's start since a redo starts its fraction again from 0, and stays hidden for the first five seconds (`DisplayText.EstimateTimeRemaining`). The percentage is rounded but only reads 100% at the very end.

Each video goes through `CompressionRunner` in two steps. `ProbeAsync` re-checks the extension, since a name can be typed into the picker, failing with a `CompressionFailedException`; then probes the source and reads its size. `CompressAsync` then takes that `SourceVideo`:

1. Builds the request from the `CompressionSettings` it's given (the queue passes `SettingsStore.Current` as each video starts) and plans the encode with Core's `CompressionPlanner`, just like the API's worker. A file size the planner can't encode (too small for the video's length) fails the video with the planner's message.
2. Creates the output folder if needed and encodes to a temporary file there, `<name>.<guid>.partial.mp4`, passing progress reports through. It keeps `.mp4` because ffmpeg picks the container from the extension.
3. On success, `OutputNaming.MoveToFreeName` moves it to `<name>.mp4`, or the first free `<name> (n).mp4`. It skips any existing file or folder, and moves with `overwrite: false`, so a file created between the check and the move is skipped too rather than replaced. That also covers a source that sits in the output folder already. The final name is chosen after the encode, not before, so nothing reserves a name during a long encode.
4. On any failure or cancellation, deletes the temporary file and rethrows.

**Output folder.** `CompressionRunner.DefaultOutputFolder` is `Dynamic Video Compressor` under `Environment.SpecialFolder.MyVideos`, falling back to `~/Videos` where that's empty (a Linux desktop without XDG user directories). Tests pass their own folder to the constructor.

**Outcome text.** A done video's `QueueItemViewModel.SizeChange` compares the output's size with the source's ("45.2 MB → 12.1 MB, 73% smaller", or "… 20% larger than the original"), and its `QueueItemViewModel.Details` is "Saved to <path>" plus any warnings; a failed one's `Details` is the reason. Sizes use 1024-based units labelled KB/MB/GB, as Windows Explorer does, and all numbers are formatted the same on every locale since the text is English. The reason follows the API worker's rule: a `CompressionFailedException`'s message as-is, anything else a generic message, with the full exception logged either way.

## Settings

One set of settings applies to the whole queue. The queue reads `SettingsStore.Current` when each video starts, so a change applies to every video still waiting and not to the one being compressed. The panel says so.

**The model.** `CompressionSettings` is an immutable record: `Quality` (a tier name), `MaxShortSide` and `MaxFps` (null for the source's own), `EncoderPreset`, `AimForFileSize` and `TargetSizeMb`. Its defaults match the API's shipped defaults (`Balanced`, short side capped at 1080 and fps at 30, preset `slow`). `CreateRequest` builds Core's `CompressionRequest`:

- Resolution and framerate always go in as caps (`DefaultMaxShortSide`, `DefaultMaxFps`), never as the exact `ShortSide`/`Fps`. Core honours an exact value as-is, upscaling included, while a desktop user picking "1080p" means "no bigger than 1080p". Null means no cap.
- The choices on offer are `ShortSideCaps` (every short side in `ResolutionDefinitions.ShortSides`, largest first) and `FpsCaps` (60, 30, 24).
- Aiming for a file size sends `TargetSizeBytes` instead of `QualityPreference`. MB are 1024 × 1024 bytes (`BytesPerMb`), matching how the app shows sizes. The size is kept while aiming for one is off, so turning it back on restores it. `TwoPass` is always on, the engine's usual choice; it isn't a setting.

**The file.** `SettingsStore` loads `%AppData%\Dynamic Video Compressor\settings.json` (`Environment.SpecialFolder.ApplicationData`) once, when it's created, and `Update` writes the whole record whenever a setting changes. It writes a `.tmp` file beside it and moves that over the file, so a crash mid-write can't leave half a file. A failed write is logged and the settings still apply for the session. The JSON uses camelCase names, and the computed `TargetSizeBytes` is left out.

Loading never fails: `SettingsStore.Parse` reads each property on its own from a `JsonObject` instead of deserializing the record, so one bad value doesn't throw away the rest.

- A missing file means a first run, and gives the defaults silently. An unreadable file, text that isn't JSON, or JSON that isn't an object also gives the defaults, and the read failure is logged.
- A missing property, a value of the wrong type, or a value no longer on offer (an unknown tier or preset, a cap not in the lists, a size out of range) gets that setting's default.
- For the caps, an explicit `null` is a valid value, meaning the source's own, so it's told apart from a missing property.
- Tier and preset names are matched ignoring case and stored as the definitions spell them.

**The panel.** `SettingsViewModel` offers each setting as a list of `SettingOption<T>` (value, label, description). `ToString` returns the label, so a `ComboBox` shows it without a template, which sidesteps compiled bindings on a generic type. Tier labels come from splitting the tier names ("SmallerSize" becomes "Smaller size"), and preset descriptions come straight from the definitions. Every change saves at once through `SettingsStore.Update`. While the saved settings are being applied to the choices (on load and on reset), an `_applying` flag stops those changes from saving straight back. A list reporting nothing picked, which a `ComboBox` can do briefly, and an empty or out-of-range MB box both keep that setting as it was. **Reset to defaults** is enabled only while the settings differ from the defaults. The preset and file-size choices sit under an "Advanced" expander. While aiming for a file size, the quality list is disabled, with a note saying why.

## Closing with work left

Without help, ffmpeg would keep running after the app exits and leave the partial file behind, and the queue would be lost without warning. If `QueueViewModel.CloseWarning` isn't null (a video is waiting or being compressed), `MainWindow.OnClosing` cancels the close and shows `ConfirmDialog` with that warning. **Keep open** is the highlighted button and what Escape picks. On **Close anyway** it awaits `QueueViewModel.StopAsync` (which cancels the compression and waits for its cleanup), then closes again. A second close request while the dialog is up is ignored. Closing with only finished videos doesn't ask.

Cancelling makes FFMpegCore kill ffmpeg and throw `OperationCanceledException` straight away, before the process has fully exited (measured: it's gone about 60-80ms later). So the temporary file's delete retries for up to five seconds in case ffmpeg still holds it. Any exception after a cancel counts as the cancel, as in the API worker. If the app itself is killed, nothing can stop ffmpeg; the README tells users the `.partial.mp4` is safe to delete.

## Comments

The root `CLAUDE.md`'s comment rules apply. `///` goes on the public members of `Services/` and `ViewModels/`. Views and `App` use ordinary `//` comments.

## Tests

`tests/Dvc.Desktop.Tests` needs no ffmpeg. It covers:

- `FfmpegCheck.HasEncoder`'s parsing, and `FfmpegCheck.CheckAsync` and `MainWindowViewModel.CheckFfmpegAsync` with missing executables. The working-ffmpeg path depends on the machine, so it isn't tested.
- `OutputNaming`: the extension change, collision numbering, and never overwriting, in a temporary folder.
- `CompressionRunner` and `QueueViewModel`, with a fake `IVideoCompressionService` (`TestSupport.cs`) whose probe returns a fixed source, fails, or holds until released, and whose encode writes a partial file, reports set progress, and then succeeds, throws, or holds until released or cancelled: naming, cleanup on failure and cancellation, processing order, pause and resume, stop now, remove, retry, clear finished, skipped files, failure messages, the close warning, probing on add (details, failing on add, waiting for a slow probe, probing once), progress reaching the item, the size change, settings being read as each video starts (the fake records each plan), and a file size too small to encode. Tests give the queue a `SettingsStore` over a temporary file (`TestSettings.Store`).
- `CompressionSettings`: the defaults, and the request each choice builds, including the file-size mode.
- `SettingsStore`: the save and load round trip, the JSON written, a failed write, a missing file, corrupt files, invalid values falling back one at a time, missing properties, explicit null caps and case-insensitive names.
- `SettingsViewModel`: the choices and their labels, showing saved settings, saving on change, the file-size mode, an empty or invalid MB box, and Reset to defaults. Everything up to a held probe or encode runs synchronously, so most tests need no waiting; tests that release or cancel one, or wait for a progress report (`Progress<T>` posts to the thread pool without a UI thread), poll with `Wait.UntilAsync`.
- `QueueItemViewModel.ReportProgress`: percentages, stages, unknown duration and the time remaining, including after a redo.
- `DisplayText`: file sizes, durations, source details, size changes and the time remaining.

The real encode, the file picker, drag and drop, the views (including the settings panel's bindings) and the close confirmation aren't covered by tests; check them by hand.

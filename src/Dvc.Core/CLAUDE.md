# Dvc.Core

The compression engine shared by the apps (`Dvc.Api`, and the planned desktop app). See the root `CLAUDE.md` for the repo as a whole: commands, git workflow and shared conventions. Paths below are relative to `src/Dvc.Core/`.

## What this is

A plain class library holding the lookup tables (`Dvc.Core.Definitions`) and probing, planning and encoding (`Dvc.Core.Compression`). It has no ASP.NET, EF Core or configuration references and knows nothing of jobs: its only packages are FFMpegCore and `Microsoft.Extensions.Logging.Abstractions`, and every setting reaches it already resolved, in a `CompressionRequest`. The API's `JobProcessingWorker` builds that request from a job and its config; the desktop app will build it from the user's choices.

## Compression

`Compression/` runs in three steps, split so every decision is testable without ffmpeg:

- `VideoCompressionService.ProbeAsync` — `FFProbe.AnalyseAsync` into a `SourceInfo`: duration, dimensions (rotation-corrected via `DisplayDimensions`, since phones store portrait video as rotated landscape frames), framerate, audio presence and bitrate, and the source video bitrate (the stream's own, else the container's total minus the audio it'll be encoded at).
- `CompressionPlanner` — pure. `PlanOutput` resolves the output dimensions, fps, audio bitrate and preset (`OutputSettings`; see "Parameter philosophy" below). `PlanEncode` picks the `EncodeMode` — `ConstantQualityEncode` (CRF and maxrate) or `TargetSizeEncode` (bitrate, capped at the source's, plus that cap's warning) — and throws `CompressionFailedException` for a target size it can't encode (unknown duration, no room for video). Two steps so the caller has the output even when the second throws. `PlanEncode` returns a `CompressionPlan` (input path, source, output, mode, warnings).
- `VideoCompressionService.EncodeAsync` — runs the plan, returning a `CompressionResult` (warnings, output size). The preset is passed as `-preset`; `-r` only when `OutputSettings.ForceFps` is set, so variable-framerate sources keep their own timing otherwise. A libx264 + AAC encode in one of two modes depending on what drives the compression (see "`TargetSizeBytes` vs `QualityPreference`" below):
  - **`QualityPreference` → CRF.** A single constant-quality pass at the tier's CRF (`QualityPreferenceDefinitions.ResolveCrf`), with a VBV `-maxrate`/`-bufsize` ceiling from `VideoEncodingCalculator.CrfMaxRateBps` that only bounds the worst case on unusually complex content. No bitrate clamping, no size correction, no two-pass — there's no requested bitrate or size to hit, and output size is deliberately content-dependent.
  - **`TargetSizeBytes` → ABR.** Everything below applies to this mode only. Two-pass (pass 1 analysis-only to a null muxer, pass 2 does the real encode against the same passlog) vs one-pass (single ABR encode) is controlled by `CompressionRequest.TwoPass`, which the API fills from its `Encoding:TwoPass` config setting (default `true`) — an operator-level tradeoff, not a per-compression option, since it trades encode time (two-pass measured roughly 1.7-1.8x slower on test clips) for the encoder having a full-clip complexity profile up front rather than only a running average. VBV constraints (`-maxrate`/`-bufsize`, derived from the target bitrate) apply to both modes and bound how far the encoder can drift from the target within a buffer window. Neither pass mode can guarantee an exact output size up front, so an overshoot triggers a corrective re-encode at a reduced bitrate (bounded at 2 corrective attempts, 3 encodes total) rather than being reported after the fact — a caller relying on a hard size cap (e.g. a platform upload limit) should never get back an oversized file. A warning (`CompressionResult.Warnings`) is returned only once the encode actually succeeds, noting that correction was needed; if every attempt still overshoots, or a correction would need a video bitrate below 1kbps (the encoder can't get small enough at that resolution/fps), the encode fails outright with a `CompressionFailedException` instead. A target too small to leave any video budget after audio fails the same way, in `PlanEncode`, before encoding. The two-pass passlog is named with a fresh GUID per encode, so concurrent encodes never share one. The 1kbps minimum isn't a quality floor: ffmpeg takes the bitrate in whole kbps, and `-b:v 0k` makes libx264 silently fall back to constant-quality mode, ignoring the size target entirely.
`Compression/VideoEncodingCalculator` holds the pure, side-effect-free bitrate/dimension/framerate math the planner uses, unit tested directly (no I/O). It works on pixel dimensions, never resolution labels, so it applies to any aspect ratio or (eventually) custom output size:

- `BitrateFromTargetSize` — converts a target file size in bytes + duration into a video bitrate, reserving room for audio and applying a safety buffer coefficient.
- `ResolveAudioBitrateBps` — the AAC bitrate for every encode: the source's own, capped at 192kbps (`MaxAudioBitrateBps`), or 128kbps when ffprobe can't report it. A source with no audio stream gets 0 — nothing reserved from a target size's budget, and the encode passes `-an`. The cap keeps a lossless/PCM source (~1.5Mbps) from carrying its rate over to AAC and, when aiming for a target size, eating most of the video budget. The same value is what `BitrateFromTargetSize` reserves for audio.
- `CapAtSourceBitrate` — target size only: caps the size-derived bitrate at the source's own bitrate when known (encoding above what the source carries can't recover detail it doesn't have), reporting whether it fired so a warning can say the output will come in under target. It's the only adjustment a target-size bitrate gets: there's deliberately no quality floor and no output-size ceiling, since an explicit target is encoded as given.
- `MaxVideoBitrateBps` — quality tier only: bitrate ceiling for given output dimensions + fps: 12 Mbps at 1080p30, scaled by `(pixels / 1080p pixels)^0.8 × (fps / 30)^0.6` (bitrate needs grow sub-linearly with both).
- `CrfMaxRateBps` — quality tier only: the VBV maxrate for a CRF encode — `MaxVideoBitrateBps`, capped at the source's own bitrate when known.
- `DisplayDimensions` — swaps stored width/height for a 90/270° rotation tag.
- `ResolveShortSide` / `ResolveFps` — an explicit request wins as-is (including upscaling/raising); otherwise the source's value, lowered to the default cap if it exceeds it. `ResolveFps` returns null for "keep source timing".
- `CalculateOutputDimensions` — source dimensions + target short side → output `(width, height)`, preserving aspect ratio and orientation, both rounded to even (libx264/yuv420p rejects odd dimensions).

`CompressionFailedException` marks a failure the engine can explain: an unreadable file, no video stream, a size target that can't be hit. Its message is written to be shown to end users, worded for either app ("the file", not "the upload"). Any other exception from compressing is mostly FFMpegCore relaying ffmpeg's raw output, banner and build configuration included, so the apps log it and show a generic message instead. Warnings (`CompressionPlan.Warnings`, `CompressionResult.Warnings`) are distinct from failures: the compression still completed, just not exactly as requested (a target-size bitrate was capped at the source's bitrate, or output size initially overshot but was corrected).

The engine logs through `ILogger<VideoCompressionService>` and never mentions a job: an app that wants its lines tagged wraps the work in a logging scope (the API does, per job).

### Parameter philosophy

One rule instead of clamps: **anything a caller leaves out gets a safe, source-aware default; anything a caller sets is used exactly as given.**

- **Standard** parameters (quality tier, short side, fps) cover the 99% case — "take this file and compress away excess data" — and are safe to leave unset. An unset `CompressionRequest.ShortSide`/`Fps` follows the source, lowered to `DefaultMaxShortSide`/`DefaultMaxFps` if the source exceeds them, never raised. The API fills those caps from `Encoding:DefaultMaxShortSide`/`Encoding:DefaultMaxFps`.
- **Advanced** parameters (target size, encoder preset) and any explicitly set standard parameter are honored literally — the engine acts as an enhanced ffmpeg interface there, including upscaling or raising fps if asked. Callers validate only what could never encode (the API's `JobUploadReader`).

The target-size path follows this too: the requested size always wins, with no quality floor raising the bitrate over it and no output-size ceiling lowering it. A target size without a short side uses the normal source-capped default, even when the size only affords a rough encode at that resolution; picking a smaller resolution is left to the caller.

### `TargetSizeBytes` vs `QualityPreference`

These map to the two intended uses. `QualityPreference` (plus optional short side/fps) is the standard path: re-encoding uploads to save long-term storage and standardize format, where "only as many bits as the content needs" matters and exact output size doesn't. `TargetSizeBytes` is the advanced path, for cases like fitting a gameplay clip under a 10MB upload limit, where a hard size cap is the whole point. The size is an exact byte count, so the engine never chooses between MB and MiB: the caller converts from whatever unit its limit is given in, and the overshoot check compares the output file's length in bytes against it directly.

A `CompressionRequest` sets exactly one of these (`PlanEncode` throws `ArgumentException` otherwise):

- **`TargetSizeBytes`** → needs the source duration to convert to a bitrate, so that conversion (`BitrateFromTargetSize`) happens in `CompressionPlanner.PlanEncode` after probing. The raw bitrate then goes through `VideoEncodingCalculator.CapAtSourceBitrate` before an ABR encode.
- **`QualityPreference`** → resolved to a CRF via `QualityPreferenceDefinitions.ResolveCrf`, no duration needed, and never clamped.

## Definitions

`Definitions/` holds static lookup tables (plus small pure helpers over them), no I/O. Each value carries a user-facing description, which the API exposes through `GET /v1/options`:

- `ResolutionDefinitions` — the accepted resolution labels (`360p`…`4K`) and the **short side** each sets; the long side follows the source's aspect ratio. Labels exist only at the apps' boundary — the API's `JobProcessingWorker` resolves them to pixels for `CompressionRequest.ShortSide`, and nothing in `Dvc.Core.Compression` sees them.
- `QualityPreferenceDefinitions` — the quality tiers (`SmallerSize` 26, `Balanced` 23, `HigherQuality` 20 at 1080p; default `Balanced`) as libx264 CRF values. `ResolveCrf` offsets them by one per doubling of pixel count relative to 1080p, clamped to −1…+2, since the same CRF looks cleaner at higher resolutions. No fps adjustment — CRF already accounts for framerate. Only used when no target size is set.
- `EncoderPresetDefinitions` — the accepted libx264 presets with descriptions, and the fallback default (`slow`) when an app has no preset configured.
- `VideoFileDefinitions` — the file extensions ffmpeg reads reliably (`SupportedExtensions`), checked at an app's boundary so an obviously-wrong file (`.txt`, `.zip`) fails fast instead of deep inside ffprobe.

## Conventions

- **`Dvc.Core` docs describe the engine.** Core can't see the apps' types, so its doc comments never name them (`SubmitJobRequest`, `EncodingOptions`) or speak of "the API", "jobs" or "clients". The API's own docs point at the Core tables instead (`SubmitJobRequest.Resolution` names `ResolutionDefinitions.ShortSides`), and the API-facing wording lives there and in the `GET /v1/options` descriptions.

## Tests

`tests/Dvc.Core.Tests` covers `VideoEncodingCalculator`, `CompressionPlanner` and the `Definitions` tables — pure unit tests, no ffmpeg. `VideoCompressionService` (probing and encoding) has no tests of its own; the API's `scripts/Test-Setup.ps1` exercises it end to end.

## Planned features

- **Custom output resolution.** An explicit width × height (e.g. `900x400`) alongside the resolution labels, mainly for the planned desktop app, where the user can see the source's shape. It's an additional path, not a replacement: the short side still backs the labels and the default short-side cap, which callers that don't know the source's shape depend on.
  - What changes: a shared parser in `Definitions`, so the apps' validation and the engine agree; `CompressionRequest`, which only carries a short side; and in the API, `JobUploadReader` validation, the `ResolutionDefinitions.ShortSides[job.Resolution]` lookup in `JobProcessingWorker.BuildCompressionRequest` (which would throw on anything but a label), and `GET /v1/options`, which advertises `Resolution` as a closed list.
  - What doesn't: the bitrate and CRF math works from pixel count, and the target-size path needs no dimensions. Only `CalculateOutputDimensions` is short-side based, and an explicit size bypasses it. Its round-to-even step is still needed, since libx264 rejects odd dimensions. Reusing the API's `Resolution` string field needs no migration.
  - Decisions to make first:
    - A size whose aspect ratio differs from the source's: stretch (the literal reading, per "Parameter philosophy"), fit within the box, or pad/crop.
    - Orientation: whether `900x400` on a portrait source means exactly that, or long × short matched to the source.
    - In the API, the same `Resolution` field, or separate `Width`/`Height` fields, which would need cross-field rules.
  - To verify: ffmpeg's scaler adjusts the sample aspect ratio to preserve the display shape by default, so a stretched output may still play back at the source's aspect unless `setsar=1` is added.

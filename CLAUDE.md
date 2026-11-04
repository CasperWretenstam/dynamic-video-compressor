# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A video compressor that encodes to a named quality tier (or, as an advanced option, a target file size), using FFmpeg under the hood. It's a from-scratch C# port of an existing Python/Tkinter desktop tool — only the compression math and general approach carry over. The repo holds a shared compression engine and the apps built on it:

- **`src/Dvc.Core`** — the compression engine: lookup tables (`Dvc.Core.Definitions`) and probing, planning and encoding (`Dvc.Core.Compression`). A plain class library with no knowledge of jobs, databases or config. See `src/Dvc.Core/CLAUDE.md`.
- **`src/Dvc.Api`** — an ASP.NET Core Web API that runs the engine as a microservice downstream of an upload system: a caller (e.g. a website's backend) posts a video and polls for the compressed result. See `src/Dvc.Api/CLAUDE.md`.
- **`Dvc.Desktop`** (planned) — a desktop app calling the engine in-process.

Each app references `Dvc.Core`, never the other way round. Each project's own `CLAUDE.md` covers its architecture and design decisions, with paths relative to that project's folder; this file covers the repo as a whole.

Requires the .NET 10 SDK, and real `ffmpeg`/`ffprobe` binaries on PATH (see "External dependency: FFmpeg" below) — neither is bundled or checked in. Alternatively, `docker compose up -d --build` runs the API in a container with both bundled — see the root `README.md` for that path.

`README.md` is the documentation for people running or calling the service: setup, the API and its parameters, error format, configuration, limitations, plus a short developer quick start. The `CLAUDE.md` files are for working on the code: architecture, design decisions and conventions. When a change is visible to callers or operators (an endpoint, a parameter, an error, a config setting, the Docker setup), update the README in the same change.

## Layout

- `src/Dvc.Core`, `src/Dvc.Api` — the projects above.
- `tests/Dvc.Core.Tests`, `tests/Dvc.Api.Tests` — one xUnit test project per project; each project's `CLAUDE.md` says what its tests cover.
- `scripts/` — PowerShell tooling for checking and poking at a running API instance (see `src/Dvc.Api/CLAUDE.md`).
- `Directory.Build.props`, `Directory.Packages.props` — build settings and package versions shared by every project (see "Conventions").
- `Dockerfile`, `docker-compose.yml` — the API's container image (see `src/Dvc.Api/CLAUDE.md`).
- `DynamicVideoCompressor.slnx` — the solution.

No CI config exists yet.

## Commands

Build: `dotnet build DynamicVideoCompressor.slnx`

Run all tests: `dotnet test`

Run a single test: `dotnet test --filter "FullyQualifiedName~VideoEncodingCalculatorTests.CalculateOutputDimensions_PreservesAspectRatioAndOrientation"` (substitute the class/method name; `--filter "MethodName"` also works for a unique method name)

Running the API and managing its database migrations: see "Commands" in `src/Dvc.Api/CLAUDE.md`.

## Git workflow

- `main` is the only long-lived branch and always builds and passes tests.
- Each change goes on a short-lived branch named `<type>/<short-description>` (`feat/job-priority`, `fix/delete-race`), squash-merged into `main` so it lands as one commit, then deleted.
- Commit messages, including squash-merge titles, follow Conventional Commits: `type(scope): summary`, e.g. `feat(api): add job priority`.
  - Types: `feat`, `fix`, `refactor`, `docs`, `test`, `build` (build system, dependencies), `chore`.
  - Scopes: `api`, `core`, `desktop`, `scripts`, `docker`. Leave the scope out for repo-wide changes.
  - A breaking change to the HTTP API is marked with `!` (`feat(api)!: …`) and ships as a new `/v2` route group (see "Request lifecycle" in `src/Dvc.Api/CLAUDE.md`).
- Releases are annotated tags per app, created on `main` with Semantic Versioning: `api/vX.Y.Z` and `desktop/vX.Y.Z`. `Dvc.Core` isn't released on its own; it ships inside each app. The API's release version is independent of its `/v1` route version.

## Conventions

- Settings shared by every project live at the repo root: `Directory.Build.props` sets `TargetFramework`, `Nullable` and `ImplicitUsings`, and `Directory.Packages.props` holds every package version (central package management), so a `.csproj` only adds what's specific to it and references packages without a `Version`. To add or upgrade a package, add or change its `PackageVersion` in `Directory.Packages.props`; `dotnet add package` does this automatically. MSBuild imports only the nearest `Directory.Build.props` above a project, so don't add one inside a project folder unless it imports the root one. The `Dockerfile` copies both files before `dotnet restore`.
- Namespace matches folder path throughout (a class under `src/Dvc.Api/Infrastructure/Data/` lives in `Dvc.Api.Infrastructure.Data`, not a flatter namespace). Keep them in sync when moving files, including the string-literal entity name EF Core's migration snapshot embeds (e.g. `modelBuilder.Entity("Dvc.Api.Models.Entities.Job", ...)`), or `dotnet ef` will see a spurious model change.
- Prefer the `required` modifier over `= default!` for properties that must be set by the caller (e.g. `Job.InputPath`, `CompressionRequest.InputPath`).
- Static `Definitions` classes (`Dvc.Core.Definitions`, plus the API-only `Models/Definitions/UploadDefinitions`) hold fixed lookup tables (resolutions, quality tiers, allowed extensions) rather than scattering magic strings/numbers through the services that use them; dictionary keys are `StringComparer.OrdinalIgnoreCase` throughout so resolution/preference labels are case-insensitive end to end.
- Comments (each project's `CLAUDE.md` adds the rules specific to it):
  - **Explain the code, briefly.** A comment says what the code does or why, in as few lines as that takes. No references to docs (CLAUDE.md, the README), plans, project progress or past versions of the code.
  - **Write for a reader opening the file cold**, not someone who already knows the project. Avoid project shorthand ("a TargetSizeBytes job", "startup recovery") and say what it means instead ("a job aiming for a file size", "the next startup re-runs jobs it finds still processing").
  - **Don't name a member bare.** `TargetSizeBytes` exists on `SubmitJobRequest`, `Job`, `JobSettings` and `CompressionRequest`, so the bare name doesn't say which one is meant or what it is. In a `///` comment, write it in `<c>` next to a plain description that carries the sentence on its own: "aims for a file size (`<c>SubmitJobRequest.TargetSizeBytes</c>`)", not "one of `<c>QualityPreferenceDefinitions.Tiers</c>`". Use the name the comment's reader knows it by: in docs written for API callers, the JSON or form field name (`<c>targetSizeBytes</c>` for a response field, `<c>TargetSizeBytes</c>` for the submitted one); elsewhere `Type.Member`, leaving out the type for a member of the same type, and the bare name for a parameter. No `<see cref>` or `<paramref>`: the OpenAPI generator renders a cref as the member's type and qualified name (e.g. `JobSettings JobStatusResponse.Settings`), ignoring any link text, and one form everywhere keeps the docs consistent. In a `//` comment, describe it in words. A type name (`JobProcessingWorker`) is fine in a `//` comment, since it's unique and searchable. API-facing strings (error messages, `GET /v1/options` descriptions) keep the field names, since those are the names callers send.
  - **Each doc comment stands on its own.** A hover shows one member's doc in isolation, and Scalar shows each field's description on its own, so don't refer back to a sibling's ("what those settings resolved to", "what that came to"). Name the thing.
  - **Where `///` goes:** the API's endpoint handlers and models (`Models/Entities`, `Models/DTOs`, `Models/Definitions`, `Models/Options`), everything public in `Dvc.Core`, and any type or member a doc comment refers to by name. Code inside the endpoints, the worker and the services explains itself with ordinary `//` comments.
  - **Tag usage:** a `<summary>` is phrased as what the member returns ("The AAC bitrate (bps) to encode audio at…"), so `<returns>` is left out unless the return value carries something the summary doesn't, e.g. a sentinel value or the meaning of a tuple field (`CapAtSourceBitrate`). `<param>` is added only where the name alone isn't enough: units, or what `null` means. The API's handlers and the models reaching its OpenAPI document have rules of their own (see `src/Dvc.Api/CLAUDE.md`).
  - **Enforcement:** `GenerateDocumentationFile` is on in every project, so every build checks the doc comments' XML: malformed XML (CS1570), a `<param>` for a parameter that doesn't exist (CS1572), and a `///` comment attached to nothing (CS1587). `Dvc.Api.csproj` and `Dvc.Core.csproj` suppress CS1591 (undocumented public member) and CS1573 (only some parameters documented), both deliberate here. Names written in `<c>` aren't checked, so search the comments for a member's old name when renaming it.

## External dependency: FFmpeg

FFMpegCore (the NuGet package, referenced by `Dvc.Core`) is a thin C# wrapper around the ffmpeg/ffprobe CLI — it contains no codec logic itself and does no encoding in-process. The real binaries must be installed separately and discoverable on PATH; they're intentionally never committed (large, platform-specific, frequently updated). They also need to be a build compiled with libx264 support (e.g. gyan.dev's "essentials"/"full" builds), since `Dvc.Core`'s `VideoCompressionService` hardcodes `VideoCodec.LibX264` — a minimal/LGPL-only ffmpeg build will fail at encode time. The API's Docker image bundles a suitable build (see `src/Dvc.Api/CLAUDE.md`).

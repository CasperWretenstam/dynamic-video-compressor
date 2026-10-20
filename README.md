# Dynamic Video Compressor

An HTTP microservice that compresses video files to a named quality tier (or, for advanced use, a target file size) using FFmpeg. Post a video, poll for status, download the compressed result — designed to sit downstream of another backend (e.g. an upload service), not to be called directly by end users.

## Prerequisites

- Docker and Docker Compose

That's all the service needs: ffmpeg and the .NET runtime are bundled in the image. The optional install check (see [Verifying an install](#verifying-an-install)) also needs PowerShell and ffmpeg on the machine you run it from.

## Quick start

```bash
docker compose up -d --build
```

This builds the image, starts the service on `http://localhost:5155`, applies database migrations automatically on first boot, and persists all state (the database and uploaded/compressed files) in a named Docker volume (`dvc-data`) so it survives container restarts and rebuilds.

Check it's up:

```bash
curl http://localhost:5155/health
```

## API

- `POST /v1/jobs` (multipart) — submit a video. Returns `202 Accepted` with a job ID immediately; compression happens in the background.
- `GET /v1/jobs/{id}` — poll job status (`Queued`, `Processing`, `Completed`, `Failed`), plus `warning`/`errorMessage` if applicable. Also returns the job's `settings` (as submitted; a null `resolution`/`fps` means "follow the source") and its `output` dimensions and framerate once processing has worked them out:

  ```json
  {
    "status": "Completed",
    "settings": { "qualityPreference": "Balanced", "targetSizeMb": null, "resolution": null, "fps": null, "encoderPreset": "slow" },
    "output": { "width": 1080, "height": 1920, "fps": 30 }
  }
  ```
- `GET /v1/jobs/{id}/result` — once `Completed`, streams the compressed file back.
- `DELETE /v1/jobs/{id}` — deletes a job and its files. Call it once you've downloaded the result, since nothing is cleaned up automatically. Deleting a `Queued` job cancels it. A `Processing` job can't be deleted and returns `409 Conflict`; delete it once it has finished. Returns `204 No Content`, or `404` for an unknown ID.
- `GET /v1/options` — describes every `POST /v1/jobs` parameter: allowed values with descriptions, and the defaults this instance is actually configured with. Build client UIs from this rather than hardcoding the tables below.

Every error response is [problem details](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`), with the message in `detail`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Unknown field 'Preset'. Accepted: QualityPreference, Resolution, Fps, TargetSizeMb, EncoderPreset",
  "traceId": "00-aca1eb2c52f416c942eaabffa9f4b27e-16a6f74f0742e535-00"
}
```

`detail` is absent only for errors the framework generates itself, such as an unknown route.

### API reference

With `OpenApi__Enabled=true` (see Configuration), the service also serves:

- `/scalar` — an interactive API reference in the browser. It lists every endpoint with what it does, what each response means and what each field contains, and you can pick a video, submit it, poll the job and download the result from there, without writing curl commands.
- `/openapi/v1.json` — the OpenAPI document the page is built from, for generating clients or importing into other tools.

Both are off by default. To turn them on in Docker, uncomment the two `environment:` lines in `docker-compose.yml` and run `docker compose up -d`, then open `http://localhost:5155/scalar`. A local `dotnet run` has them on already (see Development).

### Job parameters

Only `file` is required. Leaving everything else out is the intended default: the video is re-encoded at `Balanced` quality, keeping its own aspect ratio, resolution and framerate, but brought down to the configured caps (1080p, 30fps by default) if it exceeds them.

**Standard** — for compressing away excess data and standardizing stored media. Safe to leave unset.

| Parameter | Values | When omitted |
|---|---|---|
| `file` | The video (`.mp4`, `.mov`, `.mkv`, ...; see `GET /v1/options`) | Required |
| `QualityPreference` | `SmallerSize`, `Balanced`, `HigherQuality` | `Balanced` |
| `Resolution` | `360p`, `480p`, `720p`, `1080p`, `1440p`, `4K` | Source resolution, short side capped at `Encoding__DefaultMaxShortSide` (see Configuration). Never upscaled |
| `Fps` | Any number above 0 (`29.97` is fine) | Source framerate, capped at `Encoding__DefaultMaxFps`. Never raised |

- `QualityPreference` sets the visual quality to aim for, not a size: output size follows the content, so a static clip shrinks far more than shaky, detailed footage.
- `Resolution` sets the **short side**; the long side follows the source's aspect ratio. `1080p` is 1920×1080 for landscape 16:9, 1080×1920 for portrait, and 2560×1080 for 21:9. This way a label works for any orientation or aspect ratio, without knowing the source's shape.

**Advanced** — direct control. Values that are set are used exactly as given, including upscaling or raising the framerate if that's what's asked for.

| Parameter | Values | When omitted |
|---|---|---|
| `TargetSizeMb` | Any number above 0 | No size target |
| `EncoderPreset` | `ultrafast` … `medium` … `slow` … `veryslow`, `placebo` | `Encoding__Preset` (`slow` by default) |

- `TargetSizeMb` compresses to at most this many MB, e.g. to fit an upload limit. It takes priority over `QualityPreference`: quality follows from the size instead.
- `EncoderPreset` is how much effort the encoder spends finding redundancy, independent of quality. Slower presets give smaller files at a given `QualityPreference`, or better quality at a given `TargetSizeMb`, at the cost of encode time. `slow` is roughly 1.5–2× `medium`'s encode time for files several percent smaller; past `slower`, the gains are small.

Field names are matched ignoring case, so `encoderPreset` works too. Invalid values (an unknown `EncoderPreset`, `Fps=abc`, `Fps=0`), unknown field names (a typo like `Resoluton`) and a field sent more than once (including `Resolution` and `resolution` together) are rejected with `400 Bad Request` and a message saying what's wrong.

**Send the parameters before `file`.** Each field is validated the moment it arrives, so a bad value is rejected before the video starts uploading. Fields sent after the file are still validated, just only once the whole upload has been received. Uploads are capped at 2GB: a request whose `Content-Length` declares more is rejected with `413 Payload Too Large` before any of it is read.

### Verifying an install

```powershell
./scripts/Test-Setup.ps1
```

Checks a running instance end to end, in about a minute. It generates a few small test clips and submits them as different kinds of job. Then it checks each result and the error handling, and deletes the jobs again so nothing is left on the service. Every check prints a pass/fail line, and the script exits non-zero if any failed. Pass `-BaseUrl` to check an instance other than `http://localhost:5155`.

The script runs on the machine you check from, not inside the container, so that machine needs:

- PowerShell: Windows PowerShell 5.1 (built into Windows) or [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) on Linux and macOS (run it with `pwsh ./scripts/Test-Setup.ps1`).
- `ffmpeg` and `ffprobe` on PATH, to generate the clips and inspect the results. Any build works.

If Windows refuses to run the script because of its execution policy, use `powershell -ExecutionPolicy Bypass -File scripts/Test-Setup.ps1`.

To submit a single job by hand, use `./scripts/Submit-TestJob.ps1` (see `Get-Help ./scripts/Submit-TestJob.ps1 -Detailed`), or plain curl:

```bash
curl -X POST http://localhost:5155/v1/jobs -F "QualityPreference=Balanced" -F "file=@/path/to/video.mp4"
```

## Configuration

The image's defaults work as-is, so no configuration is required to get started. To change one, add it as an environment variable under `environment:` on the `dvc-api` service in `docker-compose.yml` (double underscore separates config sections, per ASP.NET Core convention):

| Variable | Default | Purpose |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Data Source=/data/dvc.db` | SQLite database location. Keep it under `/data` (see Data persistence) |
| `Storage__InputDirectory` | `/data/Storage/Input` | Uploaded files, deleted once each job finishes |
| `Storage__OutputDirectory` | `/data/Storage/Output` | Compressed output files, kept until the job is deleted (see Limitations) |
| `Database__AutoMigrate` | `true` | Applies pending EF Core migrations on startup |
| `Encoding__TwoPass` | `true` | `TargetSizeMb` jobs only: two-pass encoding is slower but hits the target bitrate more accurately; set to `false` for faster, less precise encodes. `QualityPreference` jobs are always single-pass |
| `Encoding__Preset` | `slow` | Encoder preset for jobs that don't set `EncoderPreset`. Drop to `medium` or `fast` if the queue backs up |
| `Encoding__DefaultMaxShortSide` | `1080` | Short-side cap (px) for jobs that don't set `Resolution`. Set to empty (`Encoding__DefaultMaxShortSide=`) to keep source resolution |
| `Encoding__DefaultMaxFps` | `30` | Framerate cap for jobs that don't set `Fps`. Set to empty to keep source framerate |
| `OpenApi__Enabled` | `false` | Serves the interactive API reference at `/scalar` and the OpenAPI document at `/openapi/v1.json` (see API reference) |

Invalid `Encoding__*` values (an unknown preset, a cap of 0) stop the service at startup with a message naming the setting, rather than failing jobs later.

## Data persistence

All state lives under `/data` inside the container, backed by the `dvc-data` named volume:

```bash
docker compose down       # stops the container, keeps the volume
docker compose up -d      # data (jobs, files) is still there
docker compose down -v    # removes the volume too - full reset
```

The container runs as a non-root user (UID 1654), and `/data` is the only place it can write. To use a host folder instead of the named volume, make sure that user can write to it.

## Known limitations

- **No authentication.** This service is designed to run on a trusted internal network, called only by another backend — not to be exposed directly to the internet.
- **No automatic cleanup.** A job and its compressed file stay until the caller deletes them with `DELETE /v1/jobs/{id}`; there's no expiry. A caller that never deletes its jobs will slowly fill `Storage__OutputDirectory`.
- **Single sequential worker.** Jobs are processed one at a time, in submission order. That's by design: a single ffmpeg encode already uses all available CPU cores, so running several at once wouldn't finish them sooner.

## Development

Working on the code without Docker needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), the EF Core tools (`dotnet tool install --global dotnet-ef`), and `ffmpeg`/`ffprobe` on PATH, built with libx264 (on Windows, e.g. gyan.dev's "essentials" build).

```bash
dotnet ef database update --project src/Dvc.Api   # create or update the local database
dotnet run --project src/Dvc.Api                  # serves http://localhost:5155
dotnet test
```

Run the database update once after cloning, and again whenever new migrations come in. A local run has the API reference turned on, at `http://localhost:5155/scalar`. [CLAUDE.md](CLAUDE.md) covers the architecture, design decisions and code conventions. Claude Code loads it automatically, and it's the place to read up before changing the code.

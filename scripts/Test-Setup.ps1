#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies a running Dynamic Video Compressor instance end to end.

.DESCRIPTION
    Generates a handful of small test clips covering the main kinds of job,
    submits them back to back, waits for each, checks the results, and then
    deletes the jobs again. Talks to the service over HTTP only, so it works
    the same against Docker, a local `dotnet run`, or another host.

    Checks that:
      - the service responds, and GET /v1/options describes the job parameters
      - invalid input is rejected with 400 before a job is queued
      - default, TargetSizeBytes, explicit-settings and silent jobs complete
        with the expected output, and a TargetSizeBytes result stays under its
        target
      - a video-less upload fails with an error message, without stalling the queue
      - jobs finish in the order they were submitted
      - DELETE cancels a queued job and removes finished ones

    Needs ffmpeg and ffprobe on PATH on the machine running it. Takes about a
    minute at the default encoder preset. Exits 0 if every check passed, 1 otherwise.

.PARAMETER BaseUrl
    The service to check.

.PARAMETER TimeoutSeconds
    How long to wait for each job to finish.

.PARAMETER KeepJobs
    Leaves the jobs and their output on the service instead of deleting them.

.PARAMETER KeepFiles
    Keeps the generated clips and downloaded results in the temp folder.

.EXAMPLE
    ./scripts/Test-Setup.ps1

.EXAMPLE
    ./scripts/Test-Setup.ps1 -BaseUrl http://media-host:5155 -KeepJobs
#>
param(
    [string]$BaseUrl = 'http://localhost:5155',
    [int]$TimeoutSeconds = 300,
    [switch]$KeepJobs,
    [switch]$KeepFiles
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DvcScripts.psm1') -Force

$script:PassedCount = 0
$script:FailedCount = 0

# Runs one check, printing a pass/fail line. The check throws to fail, and
# may return a short detail to show after its name. Returns whether it passed.
function Invoke-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Check
    )

    try {
        $detail = & $Check
        $script:PassedCount++
        $message = if ($detail) { "$Name - $detail" } else { $Name }
        Write-DvcStatus $message -Level Success
        $true
    }
    catch {
        $script:FailedCount++
        Write-DvcStatus "$Name - $($_.Exception.Message)" -Level Failure
        $false
    }
}

function Assert-True {
    param(
        $Condition,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    if (-not $Condition) {
        throw $FailureMessage
    }
}

function Format-Megabytes([long]$Bytes) {
    '{0:N2} MB' -f ($Bytes / 1MB)
}

# Expects POST /v1/jobs to reject the upload with 400, for the reason given
# by MessagePattern. A job accepted by mistake is tracked for cleanup like
# any other.
function Test-Rejected {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [System.Collections.IDictionary]$Fields = @{},
        [Parameter(Mandatory)][string]$MessagePattern
    )

    $content = New-DvcJobContent -FilePath $FilePath -Fields $Fields
    try {
        $response = Invoke-DvcRequest -Method Post -Path '/v1/jobs' -Content $content
    }
    finally {
        $content.Dispose()
    }

    if ($response.StatusCode -eq 202) {
        $script:JobIds.Add($response.Data.id)
    }
    Assert-True ($response.StatusCode -eq 400) "expected HTTP 400, got $($response.StatusCode)"
    $detail = if ($response.Data) { $response.Data.detail } else { $null }
    Assert-True ($null -ne $detail) "expected a problem details body, got: $($response.Body)"
    Assert-True ($detail -match $MessagePattern) "rejected for the wrong reason: $detail"
    $detail
}

function Invoke-SetupChecks {
    if (-not (Invoke-Check "Service responds at $BaseUrl" { Connect-DvcService -BaseUrl $BaseUrl })) {
        return
    }

    Invoke-Check 'GET /v1/options describes the job parameters' {
        $response = Invoke-DvcRequest -Method Get -Path '/v1/options'
        Assert-True ($response.StatusCode -eq 200) "expected HTTP 200, got $($response.StatusCode)"
        $names = @($response.Data.parameters | ForEach-Object { $_.name })
        Assert-True ($names -contains 'file' -and $names -contains 'QualityPreference') "unexpected body: $($response.Body)"
        "$($names.Count) parameters"
    } | Out-Null

    Write-DvcStatus "Generating test clips in $tempDir"
    $clips = @{
        Standard  = New-DvcTestClip -Path (Join-Path $tempDir 'standard.mp4')
        Noisy     = New-DvcTestClip -Path (Join-Path $tempDir 'noisy.mp4') -Width 640 -Height 360 -Seconds 4 -Noisy
        Portrait  = New-DvcTestClip -Path (Join-Path $tempDir 'portrait.mp4') -Width 480 -Height 640
        Silent    = New-DvcTestClip -Path (Join-Path $tempDir 'silent.mp4') -NoAudio
        AudioOnly = New-DvcTestClip -Path (Join-Path $tempDir 'audio-only.mp4') -NoVideo
    }
    $textFile = Join-Path $tempDir 'notes.txt'
    Set-Content -LiteralPath $textFile -Value 'Not a video.'

    Invoke-Check 'Rejects an unknown EncoderPreset with 400' {
        Test-Rejected -FilePath $clips.Standard.FullName -Fields @{ EncoderPreset = 'not-a-preset' } -MessagePattern 'Unsupported encoder preset'
    } | Out-Null

    Invoke-Check 'Rejects an unknown field with 400' {
        Test-Rejected -FilePath $clips.Standard.FullName -Fields @{ Preset = 'slow' } -MessagePattern 'Unknown field'
    } | Out-Null

    Invoke-Check 'Rejects a field sent twice with 400' {
        # Cased differently, since field names are matched ignoring case.
        $fields = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
        $fields['Resolution'] = '720p'
        $fields['resolution'] = '1080p'
        Test-Rejected -FilePath $clips.Standard.FullName -Fields $fields -MessagePattern 'sent more than once'
    } | Out-Null

    Invoke-Check 'Rejects an unsupported file type with 400' {
        Test-Rejected -FilePath $textFile -MessagePattern 'Unsupported file extension'
    } | Out-Null

    # Decimal 1 MB rather than 1MB (MiB), to check the target is taken as an
    # exact byte count.
    $targetSizeBytes = 1000000
    $cases = @(
        [pscustomobject]@{
            Name     = 'Default settings'
            Clip     = $clips.Standard
            Fields   = @{}
            Expected = 'Completed'
            Verify   = {
                param($job, $video)
                Assert-True $video.HasAudio 'output has no audio track'
                "$($video.Width)x$($video.Height), $(Format-Megabytes $video.SizeBytes)"
            }
        }
        [pscustomobject]@{
            Name     = "TargetSizeBytes = $targetSizeBytes"
            Clip     = $clips.Noisy
            Fields   = @{ TargetSizeBytes = $targetSizeBytes }
            Expected = 'Completed'
            Verify   = {
                param($job, $video)
                Assert-True ($video.SizeBytes -le $targetSizeBytes) "output is $($video.SizeBytes) bytes, over the $targetSizeBytes-byte target"
                "$(Format-Megabytes $video.SizeBytes) from a $(Format-Megabytes $clips.Noisy.Length) source"
            }
        }
        # Mid-queue on purpose: the jobs after it show a failure doesn't stall the queue.
        [pscustomobject]@{
            Name     = 'Upload with no video stream'
            Clip     = $clips.AudioOnly
            Fields   = @{}
            Expected = 'Failed'
            Verify   = {
                param($job, $video)
                Assert-True (-not [string]::IsNullOrWhiteSpace($job.errorMessage)) 'no errorMessage on the failed job'
                "`"$($job.errorMessage)`""
            }
        }
        [pscustomobject]@{
            Name     = 'resolution = 360p, fps = 15, encoderPreset = veryfast (portrait, camelCase names)'
            Clip     = $clips.Portrait
            Fields   = [ordered]@{ resolution = '360p'; fps = 15; encoderPreset = 'veryfast' }
            Expected = 'Completed'
            Verify   = {
                param($job, $video)
                Assert-True ($video.Width -eq 360 -and $video.Height -eq 480) "output is $($video.Width)x$($video.Height), expected 360x480"
                Assert-True ($null -ne $video.Fps -and [math]::Abs($video.Fps - 15) -lt 0.01) "output is $($video.Fps)fps, expected 15"
                "$($video.Width)x$($video.Height) at $($video.Fps)fps"
            }
        }
        [pscustomobject]@{
            Name     = 'Source with no audio track'
            Clip     = $clips.Silent
            Fields   = @{}
            Expected = 'Completed'
            Verify   = {
                param($job, $video)
                Assert-True (-not $video.HasAudio) 'output has an audio track'
                "$($video.Width)x$($video.Height), video only"
            }
        }
    )
    foreach ($case in $cases) {
        $case | Add-Member -NotePropertyName Id -NotePropertyValue $null
        $case | Add-Member -NotePropertyName Final -NotePropertyValue $null
    }

    foreach ($case in $cases) {
        Invoke-Check "Submits: $($case.Name)" {
            $job = Submit-DvcJob -FilePath $case.Clip.FullName -Fields $case.Fields
            $case.Id = $job.id
            $script:JobIds.Add($job.id)
        } | Out-Null
    }

    # Submitted last, so it's still waiting behind the others when deleted.
    Invoke-Check 'DELETE cancels a queued job' {
        $job = Submit-DvcJob -FilePath $clips.Standard.FullName
        $script:JobIds.Add($job.id)
        Remove-DvcJob -Id $job.id
        $script:JobIds.Remove($job.id) | Out-Null
        $response = Invoke-DvcRequest -Method Get -Path "/v1/jobs/$($job.id)"
        Assert-True ($response.StatusCode -eq 404) "job still exists after DELETE (HTTP $($response.StatusCode))"
    } | Out-Null

    Write-DvcStatus "Waiting for $(@($cases | Where-Object { $_.Id }).Count) jobs to finish"
    foreach ($case in $cases | Where-Object { $_.Id }) {
        Invoke-Check "$($case.Name) -> $($case.Expected)" {
            $job = Wait-DvcJob -Id $case.Id -TimeoutSeconds $TimeoutSeconds
            $case.Final = $job
            $reason = if ($job.errorMessage) { ": $($job.errorMessage)" } else { '' }
            Assert-True ($job.status -eq $case.Expected) "ended $($job.status)$reason"

            $video = $null
            if ($job.status -eq 'Completed') {
                $result = Save-DvcJobResult -Id $case.Id -OutFile (Join-Path $tempDir "result-$($case.Id).mp4")
                $video = Get-DvcVideoInfo -Path $result.FullName
                Assert-True $video.HasVideo 'downloaded result has no video stream'
            }

            $detail = & $case.Verify $job $video
            if ($job.warning) { "$detail (warning: $($job.warning))" } else { $detail }
        } | Out-Null
    }

    Invoke-Check 'Jobs finished in submission order' {
        $finished = @($cases | Where-Object { $_.Final -and $_.Final.completedAt })
        Assert-True ($finished.Count -ge 2) 'fewer than two jobs finished, nothing to compare'
        for ($i = 1; $i -lt $finished.Count; $i++) {
            $previous = [DateTimeOffset]$finished[$i - 1].Final.completedAt
            $current = [DateTimeOffset]$finished[$i].Final.completedAt
            Assert-True ($current -ge $previous) "'$($finished[$i].Name)' finished before '$($finished[$i - 1].Name)'"
        }
        "$($finished.Count) jobs"
    } | Out-Null

    if (-not $KeepJobs) {
        Invoke-Check 'DELETE removes finished jobs' {
            $count = $script:JobIds.Count
            foreach ($id in @($script:JobIds)) {
                Remove-DvcJob -Id $id
                $script:JobIds.Remove($id) | Out-Null
                $response = Invoke-DvcRequest -Method Get -Path "/v1/jobs/$id"
                Assert-True ($response.StatusCode -eq 404) "job $id still exists after DELETE (HTTP $($response.StatusCode))"
            }
            "$count jobs"
        } | Out-Null
    }
}

Write-Host "Dynamic Video Compressor setup check" -ForegroundColor White
Write-Host ''

foreach ($command in 'ffmpeg', 'ffprobe') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        Write-DvcStatus "$command not found on PATH. It's needed to generate test clips and inspect the results." -Level Failure
        exit 1
    }
}

$script:JobIds = [System.Collections.Generic.List[string]]::new()
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "dvc-test-setup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    Invoke-SetupChecks
}
catch {
    # A failure outside any check, e.g. ffmpeg failing to generate a clip.
    $script:FailedCount++
    Write-DvcStatus "Stopped early: $($_.Exception.Message)" -Level Failure
}
finally {
    # Whatever an aborted run left behind. A job still Processing can't be
    # deleted yet.
    if (-not $KeepJobs) {
        foreach ($id in @($script:JobIds)) {
            try {
                Remove-DvcJob -Id $id
            }
            catch {
                Write-DvcStatus "Could not delete job ${id}: $($_.Exception.Message)" -Level Warning
            }
        }
    }

    if ($KeepFiles) {
        Write-DvcStatus "Kept clips and results in $tempDir"
    }
    else {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($script:FailedCount -eq 0) {
    Write-Host "All $script:PassedCount checks passed." -ForegroundColor Green
    exit 0
}

Write-Host "$script:FailedCount of $($script:PassedCount + $script:FailedCount) checks failed." -ForegroundColor Red
exit 1

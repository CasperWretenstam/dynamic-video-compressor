#Requires -Version 5.1
<#
.SYNOPSIS
    Submits one job to a running Dynamic Video Compressor instance, for manual testing.

.DESCRIPTION
    Sends a video to POST /v1/jobs with whichever parameters are given, exactly
    as given, so it can also be used to see how the service handles a
    combination (e.g. both TargetSizeMb and QualityPreference). With no
    -FilePath, generates a small test clip with ffmpeg first.

    By default it returns as soon as the job is queued. -Wait polls until the
    job finishes; -OutFile also downloads the result, and -Remove deletes the
    job afterwards. The final job status is written to the pipeline.

.PARAMETER FilePath
    The video to submit. Without it, a 3-second 640x480 test clip is generated.

.PARAMETER Wait
    Polls until the job is Completed or Failed.

.PARAMETER OutFile
    Downloads the compressed result here once it's Completed. Implies -Wait.

.PARAMETER Remove
    Deletes the job from the service once it has finished (after -OutFile, if
    given). Implies -Wait.

.EXAMPLE
    ./scripts/Submit-TestJob.ps1

.EXAMPLE
    ./scripts/Submit-TestJob.ps1 -FilePath ./clip.mov -TargetSizeMb 10 -OutFile ./clip-10mb.mp4 -Remove

.EXAMPLE
    ./scripts/Submit-TestJob.ps1 -Resolution 720p -Fps 29.97 -EncoderPreset veryfast -Wait
#>
param(
    [string]$FilePath,
    [string]$QualityPreference,
    [double]$TargetSizeMb,
    [string]$Resolution,
    [double]$Fps,
    [string]$EncoderPreset,
    [switch]$Wait,
    [string]$OutFile,
    [switch]$Remove,
    [string]$BaseUrl = 'http://localhost:5155',
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DvcScripts.psm1') -Force

# Only the parameters actually passed are sent, so everything else falls
# back to the service's defaults.
$fields = [ordered]@{}
foreach ($name in 'QualityPreference', 'TargetSizeMb', 'Resolution', 'Fps', 'EncoderPreset') {
    if ($PSBoundParameters.ContainsKey($name)) {
        $fields[$name] = $PSBoundParameters[$name]
    }
}

$generatedClip = $null
try {
    Connect-DvcService -BaseUrl $BaseUrl

    if (-not $FilePath) {
        $generatedClip = New-DvcTestClip -Path (Join-Path ([System.IO.Path]::GetTempPath()) "dvc-test-clip-$([guid]::NewGuid()).mp4")
        $FilePath = $generatedClip.FullName
        Write-DvcStatus "Generated a test clip: $FilePath"
    }

    $description = if ($fields.Count -gt 0) {
        ($fields.Keys | ForEach-Object { "$_=$([System.Convert]::ToString($fields[$_], [System.Globalization.CultureInfo]::InvariantCulture))" }) -join ', '
    }
    else {
        'service defaults'
    }
    Write-DvcStatus "Submitting $FilePath ($description)"
    $job = Submit-DvcJob -FilePath $FilePath -Fields $fields
    Write-DvcStatus "Queued job $($job.id)" -Level Success

    if (-not ($Wait -or $OutFile -or $Remove)) {
        return Get-DvcJob -Id $job.id
    }

    Write-DvcStatus 'Waiting for it to finish'
    $job = Wait-DvcJob -Id $job.id -TimeoutSeconds $TimeoutSeconds
    if ($job.status -eq 'Completed') {
        Write-DvcStatus "Completed" -Level Success
    }
    else {
        Write-DvcStatus "Failed: $($job.errorMessage)" -Level Failure
    }
    if ($job.warning) {
        Write-DvcStatus $job.warning -Level Warning
    }

    if ($OutFile -and $job.status -eq 'Completed') {
        $result = Save-DvcJobResult -Id $job.id -OutFile $OutFile
        Write-DvcStatus ('Saved the result to {0} ({1:N2} MB)' -f $result.FullName, ($result.Length / 1MB)) -Level Success
    }

    if ($Remove) {
        Remove-DvcJob -Id $job.id
        Write-DvcStatus "Deleted job $($job.id) from the service"
    }

    $job
}
catch {
    Write-DvcStatus $_.Exception.Message -Level Failure
    exit 1
}
finally {
    if ($generatedClip) {
        Remove-Item -LiteralPath $generatedClip.FullName -ErrorAction SilentlyContinue
    }
}

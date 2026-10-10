#Requires -Version 5.1
<#
.SYNOPSIS
    Shared functions for the scripts in this folder: generating test clips,
    inspecting videos, and calling the Dynamic Video Compressor API.

.DESCRIPTION
    Every script here is a thin wrapper over these functions. They can also
    be used directly from an interactive session:

        Import-Module ./scripts/DvcScripts.psm1
        Connect-DvcService -BaseUrl http://localhost:5155
        $clip = New-DvcTestClip -Path ./clip.mp4
        $job  = Submit-DvcJob -FilePath $clip.FullName -Fields @{ Resolution = '720p' }
        Wait-DvcJob -Id $job.id

    Runs on Windows PowerShell 5.1 and PowerShell 7+. Clip generation and
    video inspection need ffmpeg/ffprobe on PATH; any build works, since
    test clips are encoded with codecs every build includes.
#>

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http

$script:BaseUrl = 'http://localhost:5155'
$script:Invariant = [System.Globalization.CultureInfo]::InvariantCulture

# One client for the session. No client-side timeout: uploads can be up to
# 2GB, and Wait-DvcJob applies its own timeout to polling.
$script:HttpClient = [System.Net.Http.HttpClient]::new()
$script:HttpClient.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan

#region Output

function Write-DvcStatus {
    <#
    .SYNOPSIS
        Writes a status line in the shared style: a fixed-width tag, then the message.
    #>
    param(
        [Parameter(Mandatory, Position = 0)][string]$Message,
        [ValidateSet('Info', 'Success', 'Warning', 'Failure')][string]$Level = 'Info'
    )

    $tag, $color = switch ($Level) {
        'Info' { ' .. ', 'Cyan' }
        'Success' { ' OK ', 'Green' }
        'Warning' { 'WARN', 'Yellow' }
        'Failure' { 'FAIL', 'Red' }
    }
    Write-Host "[$tag] " -ForegroundColor $color -NoNewline
    Write-Host $Message
}

#endregion

#region Prerequisites

function Assert-DvcCommand {
    <#
    .SYNOPSIS
        Throws unless every named command is on PATH, naming what it's needed for.
        Not exported: Assert isn't an approved PowerShell verb.
    #>
    param(
        [Parameter(Mandatory)][string[]]$Name,
        [Parameter(Mandatory)][string]$Purpose
    )

    $missing = @($Name | Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) })
    if ($missing.Count -gt 0) {
        throw "$($missing -join ', ') not found on PATH. Needed to $Purpose."
    }
}

#endregion

#region Test clips and inspection

function New-DvcTestClip {
    <#
    .SYNOPSIS
        Generates a synthetic test clip with ffmpeg and returns it as a FileInfo.

    .PARAMETER Noisy
        Adds heavy per-frame noise and a high bitrate, so the clip is large and
        hard to compress: a meaningful input for a TargetSizeMb job.

    .PARAMETER NoAudio
        Video only.

    .PARAMETER NoVideo
        Audio only, so the service should reject it as having no video stream.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [double]$Seconds = 3,
        [int]$Width = 640,
        [int]$Height = 480,
        [double]$Fps = 30,
        [switch]$Noisy,
        [switch]$NoAudio,
        [switch]$NoVideo
    )

    Assert-DvcCommand -Name ffmpeg -Purpose 'generate test clips'
    if ($NoAudio -and $NoVideo) {
        throw 'A test clip needs at least one of video or audio.'
    }

    $duration = $Seconds.ToString($script:Invariant)
    $ffmpegArgs = @('-y', '-loglevel', 'error')

    if (-not $NoVideo) {
        $ffmpegArgs += @('-f', 'lavfi', '-i', "testsrc2=size=$($Width)x$($Height):rate=$($Fps.ToString($script:Invariant))")
    }
    if (-not $NoAudio) {
        $ffmpegArgs += @('-f', 'lavfi', '-i', 'sine=frequency=1000:sample_rate=48000')
    }
    $ffmpegArgs += @('-t', $duration)

    # mpeg4 rather than libx264: built into every ffmpeg build, and the
    # service re-encodes whatever it's given anyway.
    if (-not $NoVideo) {
        $ffmpegArgs += @('-c:v', 'mpeg4', '-pix_fmt', 'yuv420p')
        if ($Noisy) {
            $ffmpegArgs += @('-vf', 'noise=alls=40:allf=t+u', '-b:v', '8M')
        }
        else {
            $ffmpegArgs += @('-q:v', '4')
        }
    }
    if (-not $NoAudio) {
        $ffmpegArgs += @('-c:a', 'aac', '-b:a', '128k')
    }
    $ffmpegArgs += $Path

    & ffmpeg @ffmpegArgs
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed to generate $Path (exit code $LASTEXITCODE)."
    }

    Get-Item -LiteralPath $Path
}

function Get-DvcVideoInfo {
    <#
    .SYNOPSIS
        Probes a video file with ffprobe: dimensions, framerate, duration and
        which streams it has.
    #>
    param(
        [Parameter(Mandatory)][string]$Path
    )

    Assert-DvcCommand -Name ffprobe -Purpose 'inspect videos'

    $json = & ffprobe -v error -show_entries 'stream=codec_type,width,height,avg_frame_rate:format=duration,size' -of json $Path
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe could not read $Path (exit code $LASTEXITCODE)."
    }
    $probe = ($json -join "`n") | ConvertFrom-Json

    $video = @($probe.streams | Where-Object { $_.codec_type -eq 'video' }) | Select-Object -First 1
    $hasAudio = @($probe.streams | Where-Object { $_.codec_type -eq 'audio' }).Count -gt 0

    $fps = $null
    if ($video -and $video.avg_frame_rate -match '^(\d+)/(\d+)$' -and [int]$Matches[2] -ne 0) {
        $fps = [double]$Matches[1] / [double]$Matches[2]
    }

    [pscustomobject]@{
        HasVideo        = [bool]$video
        HasAudio        = $hasAudio
        Width           = if ($video) { [int]$video.width } else { $null }
        Height          = if ($video) { [int]$video.height } else { $null }
        Fps             = $fps
        DurationSeconds = [double]::Parse($probe.format.duration, $script:Invariant)
        SizeBytes       = [long]$probe.format.size
    }
}

#endregion

#region API

function Connect-DvcService {
    <#
    .SYNOPSIS
        Checks the service responds at BaseUrl, and makes it the target of
        every other API function in this session.
    #>
    param(
        [string]$BaseUrl = 'http://localhost:5155'
    )

    $script:BaseUrl = $BaseUrl.TrimEnd('/')
    $response = Invoke-DvcRequest -Method Get -Path '/health'
    if ($response.StatusCode -ne 200) {
        throw "GET $script:BaseUrl/health returned HTTP $($response.StatusCode), expected 200."
    }
}

function Invoke-DvcRequest {
    <#
    .SYNOPSIS
        Sends one request to the service. Returns the status code, the raw body
        and, for a JSON body, its parsed form - whatever the status, so callers
        can check for expected errors. Throws only if the service can't be reached.

    .PARAMETER OutFile
        Streams a successful response body to this file instead of reading it.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Post', 'Delete')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [System.Net.Http.HttpContent]$Content,
        [string]$OutFile
    )

    $httpMethod = [System.Net.Http.HttpMethod]::new($Method.ToUpperInvariant())
    $request = [System.Net.Http.HttpRequestMessage]::new($httpMethod, "$script:BaseUrl$Path")
    $request.Content = $Content
    try {
        try {
            $response = $script:HttpClient.SendAsync(
                $request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        }
        catch {
            throw "Could not reach the service at $script:BaseUrl ($($_.Exception.GetBaseException().Message)). Is it running?"
        }

        try {
            $statusCode = [int]$response.StatusCode
            $body = $null
            $data = $null

            if ($OutFile -and $response.IsSuccessStatusCode) {
                $fileStream = [System.IO.File]::Create($OutFile)
                try {
                    # [void]: Windows PowerShell emits a $null for this call.
                    [void]$response.Content.CopyToAsync($fileStream).GetAwaiter().GetResult()
                }
                finally {
                    $fileStream.Dispose()
                }
            }
            else {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $mediaType = $response.Content.Headers.ContentType
                if ($body -and $mediaType -and $mediaType.MediaType -like '*json') {
                    $data = $body | ConvertFrom-Json
                }
            }

            [pscustomobject]@{
                StatusCode = $statusCode
                Body       = $body
                Data       = $data
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Format-DvcFailure {
    <#
    .SYNOPSIS
        Describes an unexpected response: the request, the status, and the
        service's message if it sent one. Not exported.
    #>
    param(
        [Parameter(Mandatory)][string]$Request,
        [Parameter(Mandatory)]$Response
    )

    # Errors are problem details, with the message in "detail".
    $detail = if ($Response.Data -and $Response.Data.detail) { $Response.Data.detail } else { $Response.Body }
    if ($detail) {
        "$Request returned HTTP $($Response.StatusCode): $detail"
    }
    else {
        "$Request returned HTTP $($Response.StatusCode)."
    }
}

function New-DvcJobContent {
    <#
    .SYNOPSIS
        Builds a POST /v1/jobs multipart body: the fields first, then the file,
        so the service validates every field before the upload streams.
        Numbers are formatted with the invariant culture; empty values are left out.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [System.Collections.IDictionary]$Fields = @{}
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "File not found: $FilePath"
    }

    $content = [System.Net.Http.MultipartFormDataContent]::new()
    foreach ($name in $Fields.Keys) {
        $value = $Fields[$name]
        if ($null -eq $value -or "$value" -eq '') {
            continue
        }
        $content.Add([System.Net.Http.StringContent]::new([System.Convert]::ToString($value, $script:Invariant)), $name)
    }

    $resolvedPath = (Resolve-Path -LiteralPath $FilePath).ProviderPath
    $fileContent = [System.Net.Http.StreamContent]::new([System.IO.File]::OpenRead($resolvedPath))
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
    $content.Add($fileContent, 'file', [System.IO.Path]::GetFileName($resolvedPath))

    # The content is enumerable (its parts), so it has to be returned as a
    # single object or PowerShell unrolls it.
    Write-Output $content -NoEnumerate
}

function Submit-DvcJob {
    <#
    .SYNOPSIS
        Submits a video to POST /v1/jobs and returns the accepted job (id, status).

    .PARAMETER Fields
        Job parameters by name, e.g. @{ Resolution = '720p'; Fps = 29.97 }.
        GET /v1/options lists them all.

    .EXAMPLE
        Submit-DvcJob -FilePath ./clip.mp4 -Fields @{ TargetSizeMb = 10 }
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [System.Collections.IDictionary]$Fields = @{}
    )

    $content = New-DvcJobContent -FilePath $FilePath -Fields $Fields
    try {
        $response = Invoke-DvcRequest -Method Post -Path '/v1/jobs' -Content $content
    }
    finally {
        $content.Dispose()
    }

    if ($response.StatusCode -ne 202) {
        throw (Format-DvcFailure -Request "POST /v1/jobs" -Response $response)
    }
    $response.Data
}

function Get-DvcJob {
    <#
    .SYNOPSIS
        Returns a job's current status from GET /v1/jobs/{id}.
    #>
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)][string]$Id
    )

    process {
        $response = Invoke-DvcRequest -Method Get -Path "/v1/jobs/$Id"
        if ($response.StatusCode -ne 200) {
            throw (Format-DvcFailure -Request "GET /v1/jobs/$Id" -Response $response)
        }
        $response.Data
    }
}

function Wait-DvcJob {
    <#
    .SYNOPSIS
        Polls a job until it's Completed or Failed, and returns its final status.
        Throws if it hasn't finished within TimeoutSeconds.
    #>
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)][string]$Id,
        [int]$TimeoutSeconds = 300,
        [int]$PollIntervalMs = 500
    )

    process {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ($true) {
            $job = Get-DvcJob -Id $Id
            if ($job.status -in 'Completed', 'Failed') {
                return $job
            }
            if ((Get-Date) -ge $deadline) {
                throw "Job $Id is still $($job.status) after $($TimeoutSeconds)s."
            }
            Start-Sleep -Milliseconds $PollIntervalMs
        }
    }
}

function Save-DvcJobResult {
    <#
    .SYNOPSIS
        Downloads a Completed job's compressed video from GET /v1/jobs/{id}/result.
    #>
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$OutFile
    )

    # .NET resolves relative paths against the process directory, not the
    # PowerShell location.
    $fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
    $response = Invoke-DvcRequest -Method Get -Path "/v1/jobs/$Id/result" -OutFile $fullPath
    if ($response.StatusCode -ne 200) {
        throw (Format-DvcFailure -Request "GET /v1/jobs/$Id/result" -Response $response)
    }
    Get-Item -LiteralPath $fullPath
}

function Remove-DvcJob {
    <#
    .SYNOPSIS
        Deletes a job and its files with DELETE /v1/jobs/{id}. A Queued job is
        cancelled; a Processing one can't be deleted until it finishes.
    #>
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)][string]$Id
    )

    process {
        $response = Invoke-DvcRequest -Method Delete -Path "/v1/jobs/$Id"
        if ($response.StatusCode -ne 204) {
            throw (Format-DvcFailure -Request "DELETE /v1/jobs/$Id" -Response $response)
        }
    }
}

#endregion

Export-ModuleMember -Function @(
    'Write-DvcStatus'
    'New-DvcTestClip'
    'Get-DvcVideoInfo'
    'Connect-DvcService'
    'Invoke-DvcRequest'
    'New-DvcJobContent'
    'Submit-DvcJob'
    'Get-DvcJob'
    'Wait-DvcJob'
    'Save-DvcJobResult'
    'Remove-DvcJob'
)

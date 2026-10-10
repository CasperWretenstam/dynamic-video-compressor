#Requires -Version 5.1
<#
.SYNOPSIS
    Wipes local development data and recreates an empty database. Local development only.

.DESCRIPTION
    Deletes dvc.db (with its WAL/SHM files) and every file in Storage/Input
    and Storage/Output, then re-applies the EF Core migrations. Anything else
    under Storage/ is left alone. For a Docker instance, use
    `docker compose down -v` instead.

    Asks for confirmation first, and refuses to run while Dvc.Api is running,
    since the database would be locked. -Force skips both.

.PARAMETER Force
    Skips the confirmation prompt and the running-process check.

.PARAMETER ApiProjectDir
    The API project whose data to wipe.

.EXAMPLE
    ./scripts/Reset-DevData.ps1
#>
param(
    [switch]$Force,
    [string]$ApiProjectDir = (Join-Path $PSScriptRoot '..\src\Dvc.Api')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DvcScripts.psm1') -Force

$dbPath = Join-Path $ApiProjectDir 'dvc.db'
$storageDirs = @(
    (Join-Path $ApiProjectDir 'Storage\Input'),
    (Join-Path $ApiProjectDir 'Storage\Output')
)

if (-not $Force) {
    $apiProcess = @(Get-Process -Name 'Dvc.Api' -ErrorAction SilentlyContinue)
    if ($apiProcess.Count -gt 0) {
        Write-DvcStatus "Dvc.Api is running (PID $($apiProcess.Id -join ', ')) and has the database locked. Stop it first, or pass -Force." -Level Failure
        exit 1
    }

    $answer = Read-Host 'This deletes dvc.db and every file in Storage/Input and Storage/Output. Continue? (y/N)'
    if ($answer -ne 'y') {
        Write-DvcStatus 'Aborted, nothing was deleted.'
        exit 0
    }
}

Write-DvcStatus 'Deleting dvc.db'
Remove-Item -Path "$dbPath*" -Force

foreach ($dir in $storageDirs) {
    if (Test-Path -LiteralPath $dir) {
        Write-DvcStatus "Clearing $dir"
        Get-ChildItem -LiteralPath $dir -File | Remove-Item -Force
    }
}

Write-DvcStatus 'Re-applying migrations'
& dotnet ef database update --project $ApiProjectDir
if ($LASTEXITCODE -ne 0) {
    Write-DvcStatus "dotnet ef database update failed with exit code $LASTEXITCODE." -Level Failure
    exit 1
}

Write-DvcStatus 'Development data reset.' -Level Success

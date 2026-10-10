#Requires -Version 5.1
<#
.SYNOPSIS
    Prints the Jobs table of a local development database. Local development only.

.DESCRIPTION
    Reads dvc.db directly with sqlite3, so it only works against a local
    `dotnet run` instance, not one running in Docker. Safe to run while the
    API is running, since the database is in WAL mode.

.PARAMETER Last
    Shows only the most recent N jobs. 0 shows all of them.

.PARAMETER DbPath
    The database to read. Defaults to the one `dotnet run` uses.

.EXAMPLE
    ./scripts/Show-Jobs.ps1

.EXAMPLE
    ./scripts/Show-Jobs.ps1 -Last 10
#>
param(
    [int]$Last = 0,
    [string]$DbPath = (Join-Path $PSScriptRoot '..\src\Dvc.Api\dvc.db')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DvcScripts.psm1') -Force

if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    Write-DvcStatus 'sqlite3 not found on PATH. It is needed to read the database.' -Level Failure
    exit 1
}

if (-not (Test-Path -LiteralPath $DbPath)) {
    Write-DvcStatus "No database at $DbPath. Run 'dotnet ef database update --project src/Dvc.Api' first." -Level Failure
    exit 1
}

# CreatedAt is stored as ISO 8601 text in UTC, so it sorts chronologically.
# The newest N are taken first, then put back in submission order.
$limit = if ($Last -gt 0) { "LIMIT $Last" } else { '' }
$query = @"
SELECT * FROM (
    SELECT Id,
           CASE Status
               WHEN 0 THEN 'Queued'
               WHEN 1 THEN 'Processing'
               WHEN 2 THEN 'Completed'
               WHEN 3 THEN 'Failed'
               ELSE 'Unknown'
           END AS Status,
           COALESCE(QualityPreference, TargetSizeMb || 'MB') AS Target,
           EncoderPreset,
           OutputWidth || 'x' || OutputHeight AS Output,
           OutputFps,
           CreatedAt,
           CompletedAt,
           ErrorMessage
    FROM Jobs
    ORDER BY CreatedAt DESC
    $limit
)
ORDER BY CreatedAt;
"@

& sqlite3 -header -column $DbPath $query
if ($LASTEXITCODE -ne 0) {
    Write-DvcStatus "sqlite3 failed with exit code $LASTEXITCODE." -Level Failure
    exit 1
}

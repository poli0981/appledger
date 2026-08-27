<#
.SYNOPSIS
    Records an ETL fixture with the exact keyword set AppLedger's EtwHub enables.

.DESCRIPTION
    docs/19_TESTING.md asks for replay fixtures under tests/fixtures/etl/ so the same handlers can be
    driven from recorded input rather than from a live session. Recording one needs administrator
    rights, which is why this is a script you run rather than something the build does.

    The keywords match EtwHub.StartKernelSession exactly. If they drift apart, a replay test passes
    against a recording the product would never produce - so change both together.

    WHAT ENDS UP IN THE FILE: process names, full image paths, command lines, file names and remote IP
    addresses for everything running while it records. Scrub before committing (see -Scrub) and never
    record while doing anything you would not publish.

.PARAMETER Name
    Fixture name, e.g. "idle" or "chrome-browsing". Written to tests/fixtures/etl/<Name>.etl.

.PARAMETER Seconds
    How long to record. docs/19 suggests 60 s per scenario; keep each file under 20 MB.

.PARAMETER Scrub
    Rewrite paths under the user profile to C:\Users\fixture\... after recording.

.EXAMPLE
    .\tools\record-etl.ps1 -Name idle -Seconds 60

.EXAMPLE
    .\tools\record-etl.ps1 -Name chrome-browsing -Seconds 60 -Scrub
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Seconds = 60,
    [switch]$Scrub
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Recording a kernel session needs an elevated terminal.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot 'tests/fixtures/etl'
$null = New-Item -ItemType Directory -Force -Path $outputDir
$output = Join-Path $outputDir "$Name.etl"

# Must match EtwHub.StartKernelSession: Process, Thread, ImageLoad, NetworkTCPIP, DiskIO.
# Thread is not optional - DiskIO events resolve to a process through the issuing thread.
$session = 'AppLedger-Record'
$providers = 'PROC_THREAD+LOADER+NETWORKTRACE+DISK_IO+DISK_IO_INIT'

Write-Host "Recording $Seconds s of $providers into $output"
Write-Host 'Everything running right now will appear in the file. Ctrl+C to abort.' -ForegroundColor Yellow

& logman.exe stop $session -ets 2>$null | Out-Null

& logman.exe create trace $session -ets -o $output -p '"Windows Kernel Trace"' "($providers)" -nb 16 256 -bs 1024 -mode Circular -max 20
if ($LASTEXITCODE -ne 0) { throw "logman create failed with exit code $LASTEXITCODE" }

try {
    Start-Sleep -Seconds $Seconds
}
finally {
    & logman.exe stop $session -ets | Out-Null
}

$size = [math]::Round((Get-Item $output).Length / 1MB, 1)
Write-Host "Wrote $output ($size MB)"

if ($size -gt 20) {
    Write-Warning "docs/19 asks for fixtures under 20 MB; record a shorter window or use Git LFS."
}

if ($Scrub) {
    Write-Warning @'
Scrubbing is not implemented yet: rewriting paths inside an ETL needs a relogger pass
(TraceEvent's ETWReloggerTraceEventSource), not a text substitution. Until that exists, only commit
recordings made on a machine with nothing personal open, and never commit a browsing fixture with
hostnames you would not publish.
'@
}

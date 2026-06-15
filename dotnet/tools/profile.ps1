<#
.SYNOPSIS
    Launches Hush with the performance profiler enabled and runs dotnet-counters
    alongside to capture CPU, GC, and thread pool metrics.

.DESCRIPTION
    Run this script from the Hush repo root:
        .\dotnet\tools\profile.ps1

    It will:
    1. Build Hush in Debug mode
    2. Launch Hush with HUSH_PROFILE=1
    3. Start dotnet-counters monitoring the Hush process
    4. Wait for you to close Hush, then collect the outputs

    Profiling artifacts are saved to:
    - hush-profile.log  (in-process pipeline timing)
    - hush-counters.csv (CPU, GC, thread pool metrics from dotnet-counters)
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Hush.App\Hush.App.csproj'
$outputDir  = Join-Path $repoRoot 'src\Hush.App\bin\Debug\net9.0-windows10.0.26100\win-x64'
$profileLog = Join-Path $outputDir 'hush-profile.log'
$countersLog = Join-Path $repoRoot 'hush-counters.csv'

Write-Host "`n=== Hush Performance Profiler ===" -ForegroundColor Cyan
Write-Host "Building Hush..." -ForegroundColor Yellow

dotnet build $appProject --no-restore --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Build succeeded." -ForegroundColor Green

# Clean old profiling artifacts
if (Test-Path $profileLog)  { Remove-Item $profileLog }
if (Test-Path $countersLog) { Remove-Item $countersLog }

Write-Host "`nStarting Hush with HUSH_PROFILE=1..." -ForegroundColor Yellow
$env:HUSH_PROFILE = "1"

$hushProcess = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList "run","--project",$appProject,"--no-build" `
    -PassThru `
    -NoNewWindow

Start-Sleep -Seconds 3

# Find the actual Hush.App process (dotnet run spawns a child)
$hushPid = $null
$attempts = 0
while (-not $hushPid -and $attempts -lt 10) {
    $attempts++
    $candidates = Get-Process -Name "Hush.App" -ErrorAction SilentlyContinue
    if ($candidates) {
        $hushPid = $candidates[0].Id
    } else {
        # Might still be named "dotnet"
        $hushPid = $hushProcess.Id
    }
    if (-not $hushPid) { Start-Sleep -Seconds 1 }
}

if (-not $hushPid) {
    Write-Host "Could not find Hush process. Running without dotnet-counters." -ForegroundColor Yellow
} else {
    Write-Host "Hush PID: $hushPid" -ForegroundColor Green
    Write-Host "Starting dotnet-counters (CPU, GC, ThreadPool, Exceptions)..." -ForegroundColor Yellow

    # Launch dotnet-counters in background
    $countersJob = Start-Job -ScriptBlock {
        param($processId, $outFile)
        & dotnet-counters collect `
            --process-id $processId `
            --output $outFile `
            --format csv `
            --counters "System.Runtime[cpu-usage,working-set,gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,threadpool-thread-count,threadpool-queue-length,exception-count,time-in-gc]" `
            --refresh-interval 2
    } -ArgumentList $hushPid, $countersLog

    Write-Host "`n" -NoNewline
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host "  Profiler is running!" -ForegroundColor Green
    Write-Host "  1. Do your normal dictation sessions" -ForegroundColor White
    Write-Host "  2. Try a 30-60 second session to see growth" -ForegroundColor White
    Write-Host "  3. Close Hush when done (right-click tray > Quit)" -ForegroundColor White
    Write-Host "============================================" -ForegroundColor Cyan
}

Write-Host "`nWaiting for Hush to exit..." -ForegroundColor Yellow
$hushProcess.WaitForExit()

if ($countersJob) {
    Write-Host "Stopping dotnet-counters..." -ForegroundColor Yellow
    Stop-Job $countersJob -ErrorAction SilentlyContinue
    Remove-Job $countersJob -ErrorAction SilentlyContinue
}

$env:HUSH_PROFILE = $null

Write-Host "`n=== Profiling Complete ===" -ForegroundColor Cyan

if (Test-Path $profileLog) {
    $size = (Get-Item $profileLog).Length / 1KB
    Write-Host "  Pipeline profiler: $profileLog ($([math]::Round($size, 1)) KB)" -ForegroundColor Green
} else {
    Write-Host "  Pipeline profiler log not found (profiler may not have been triggered)" -ForegroundColor Yellow
}

if (Test-Path $countersLog) {
    $size = (Get-Item $countersLog).Length / 1KB
    Write-Host "  dotnet-counters:   $countersLog ($([math]::Round($size, 1)) KB)" -ForegroundColor Green
} else {
    Write-Host "  dotnet-counters log not found" -ForegroundColor Yellow
}

Write-Host "`nShare these files with Copilot for analysis." -ForegroundColor White

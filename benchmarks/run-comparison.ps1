param(
    [Parameter(Mandatory = $true)]
    [string] $AudioFile,

    [string] $DotNetModel = "whisper-tiny",
    [string] $RustModel = "whisper-tiny",
    [string] $Language = "en"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $AudioFile)) {
    throw "Audio file '$AudioFile' does not exist."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsDir = Join-Path $repoRoot "benchmarks\results"
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dotnetOut = Join-Path $resultsDir "$stamp-dotnet.json"
$rustOut = Join-Path $resultsDir "$stamp-rust.json"

dotnet run --project (Join-Path $repoRoot "dotnet\tools\TranscriptionBenchmark\TranscriptionBenchmark.csproj") -c Release -- `
    --audio-file $AudioFile `
    --model $DotNetModel `
    --language $Language `
    --json | Set-Content -Path $dotnetOut -Encoding UTF8

cargo run --manifest-path (Join-Path $repoRoot "rust\Cargo.toml") -p hush-bench --release -- `
    --audio-file $AudioFile `
    --model $RustModel `
    --language $Language `
    --json | Set-Content -Path $rustOut -Encoding UTF8

Write-Host "Wrote .NET result: $dotnetOut"
Write-Host "Wrote Rust result: $rustOut"

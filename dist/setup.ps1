# Hush Setup Script
# https://github.com/maanavdalal/hush
#
# Downloads the Nemotron CPU speech model and prepares Hush for first use.
# Run from the same directory as Hush.App.exe.

#Requires -Version 5.1
<#
.SYNOPSIS
    Hush Setup — downloads the Nemotron CPU speech model and launches Hush.

.DESCRIPTION
    This script automates model setup for Hush voice typing:

    1. Downloads the Nemotron CPU int4 ONNX model files from HuggingFace (~700 MB)
       into the Foundry Local model cache directory.
    2. Places them in the slot that the SDK resolves for "whisper-tiny" so the app
       loads Nemotron transparently — no CLI tools required.
    3. Launches Hush.

    WHY THE SWAP?
    Foundry Local's model catalog only ships Whisper variants (GPU/CUDA).
    For CPU-only inference we use NVIDIA's Nemotron ASR model (int4-quantised),
    hosted at https://huggingface.co/jiafatom/nemotron-cpu-int4.
    By pre-populating the whisper-tiny cache slot with Nemotron files, the
    Foundry Local SDK (bundled inside Hush.App.exe) loads Nemotron instead.

    PREREQUISITES
    - Foundry Local runtime must be installed (winget install Microsoft.FoundryLocal).
      The CLI tool "foundry" is NOT required — Hush uses the SDK directly.
    - Internet access for the one-time ~700 MB model download.

.NOTES
    Run from the same folder as Hush.App.exe.
    Subsequent runs skip the download if the model is already cached.
#>

param(
    [switch]$SkipLaunch,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$HushExe = Join-Path $PSScriptRoot 'Hush.App.exe'

# ─── Helpers ──────────────────────────────────────────────────────────────────

function Write-Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [!!] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "  [FAIL] $msg" -ForegroundColor Red }

function Download-File {
    param([string]$Url, [string]$OutFile)
    $dir = Split-Path $OutFile -Parent
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $fileName = Split-Path $Url -Leaf
    Write-Host "    Downloading $fileName ..." -NoNewline

    try {
        # Use BITS for large files (resume support + progress), WebClient for small
        $resp = $null
        try { $resp = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -EA SilentlyContinue } catch {}
        $size = if ($resp -and $resp.Headers['Content-Length']) { [long]$resp.Headers['Content-Length'] } else { 0 }

        if ($size -gt 10MB) {
            Start-BitsTransfer -Source $Url -Destination $OutFile -DisplayName $fileName
        } else {
            (New-Object System.Net.WebClient).DownloadFile($Url, $OutFile)
        }
        $sizeMB = [math]::Round((Get-Item $OutFile).Length / 1MB, 1)
        Write-Host " done ($sizeMB MB)" -ForegroundColor Green
    } catch {
        Write-Host " FAILED" -ForegroundColor Red
        throw "Failed to download ${Url}: $_"
    }
}

# ─── Step 0: Verify Hush.App.exe exists ──────────────────────────────────────

if (!(Test-Path $HushExe)) {
    Write-Err "Hush.App.exe not found in $PSScriptRoot"
    Write-Err "Run this script from the folder containing Hush.App.exe."
    exit 1
}

# ─── Step 1: Determine model cache directory ─────────────────────────────────

Write-Step "Locating Foundry Local model cache..."

$aitkRoot = Join-Path $env:USERPROFILE '.aitk'
$modelParent = Join-Path $aitkRoot 'Microsoft'

# The SDK resolves whisper-tiny to this cache path.
# Model ID = openai-whisper-tiny-generic-cpu:2 → folder = openai-whisper-tiny-generic-cpu-2
$modelDirName = 'openai-whisper-tiny-generic-cpu-2'
$variantDir   = 'cpu-fp32'

# Search for existing whisper-tiny directories (may vary by SDK version).
# The SDK may cache models in different locations depending on the version.
# We search multiple known paths to find existing cached models.
$cpuModelDir = $null
$hushCacheRoot = Join-Path $env:USERPROFILE '.Hush' 'cache' 'models' 'Microsoft'
$searchRoots = @($hushCacheRoot, $modelParent, (Join-Path $aitkRoot 'models'))
foreach ($root in $searchRoots) {
    if (!(Test-Path $root)) { continue }
    $match = Get-ChildItem $root -Directory -Filter 'openai-whisper-tiny-generic-cpu*' -EA SilentlyContinue | Select-Object -First 1
    if ($match) {
        $sub = Get-ChildItem $match.FullName -Directory -Filter 'cpu*' -EA SilentlyContinue | Select-Object -First 1
        if ($sub) { $cpuModelDir = $sub.FullName; break }
    }
}

# If not found, pre-create the directory so the SDK finds it on first launch.
if (!$cpuModelDir) {
    $cpuModelDir = Join-Path $modelParent $modelDirName $variantDir
    Write-Host "  Creating cache directory: $cpuModelDir"
    New-Item -ItemType Directory -Path $cpuModelDir -Force | Out-Null

    # Write the model identity file the SDK uses to recognise this cache entry.
    $inferenceModel = @'
{
  "Name": "openai-whisper-tiny-generic-cpu:2",
  "PromptTemplate": {
    "prompt": "\u003C|startoftranscript|\u003E \u003C|en|\u003E \u003C|transcribe|\u003E \u003C|notimestamps|\u003E"
  }
}
'@
    Set-Content -Path (Join-Path $cpuModelDir 'inference_model.json') -Value $inferenceModel -Encoding UTF8
}

Write-OK "Model cache: $cpuModelDir"

# ─── Step 2: Download Nemotron CPU int4 from HuggingFace ─────────────────────

Write-Step "Setting up Nemotron CPU int4 model (~700 MB one-time download)..."

$hfBase = 'https://huggingface.co/jiafatom/nemotron-cpu-int4/resolve/main'

# All files needed from the HuggingFace repo
$nemotronFiles = @(
    'audio_processor_config.json',
    'decoder.onnx',
    'decoder.onnx.data',
    'encoder.onnx',
    'encoder.onnx.data',
    'genai_config.json',
    'joint.onnx',
    'joint.onnx.data',
    'tokenizer.json',
    'tokenizer_config.json',
    'vocab.txt'
)

# Check if Nemotron is already installed
$configPath = Join-Path $cpuModelDir 'genai_config.json'
$alreadyInstalled = $false
if ((Test-Path $configPath) -and !$Force) {
    $content = Get-Content $configPath -Raw -EA SilentlyContinue
    if ($content -match 'nemotron_speech') {
        $alreadyInstalled = $true
    }
}

if ($alreadyInstalled) {
    Write-OK "Nemotron CPU int4 is already installed. Use -Force to re-download."
} else {
    # Back up original whisper files (if they exist)
    $existingFiles = @(Get-ChildItem $cpuModelDir -File -EA SilentlyContinue)
    if ($existingFiles.Count -gt 0) {
        $backupDir = "${cpuModelDir}-whisper-backup"
        if (!(Test-Path $backupDir)) {
            Write-Host "    Backing up original files..."
            Copy-Item $cpuModelDir $backupDir -Recurse -Force
        }
    }

    # Remove old whisper-specific files that conflict with Nemotron.
    # The SDK reads config.json (model_type: whisper) and picks the wrong
    # streaming processor, causing "NemotronStreamingProcessor requires a
    # nemotron_speech model type. Got: whisper".
    $whisperLeftovers = @(
        'config.json',
        'preprocessor_config.json',
        'added_tokens.json',
        'merges.txt',
        'normalizer.json',
        'special_tokens_map.json',
        'vocab.json',
        'whisper-tiny_decoder_fp32.onnx',
        'whisper-tiny_decoder_fp32.onnx.data',
        'whisper-tiny_encoder_fp32.onnx',
        'whisper-tiny_encoder_fp32.onnx.data',
        'whisper-tiny_jump_times_fp32.onnx'
    )
    foreach ($wf in $whisperLeftovers) {
        $wp = Join-Path $cpuModelDir $wf
        if (Test-Path $wp) { Remove-Item $wp -Force }
    }

    # Download each file
    $total = $nemotronFiles.Count
    for ($i = 0; $i -lt $total; $i++) {
        $file = $nemotronFiles[$i]
        $dest = Join-Path $cpuModelDir $file
        $url  = "$hfBase/$file"

        # Skip if file already exists with content (resume support)
        if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0 -and !$Force) {
            Write-Host "    [$($i+1)/$total] $file - cached, skipping"
            continue
        }

        Write-Host "    [$($i+1)/$total] " -NoNewline
        Download-File -Url $url -OutFile $dest
    }

    Write-OK "Nemotron CPU int4 model installed."
}

# ─── Step 3: Verify ──────────────────────────────────────────────────────────

Write-Step "Verifying model files..."

$required = @('encoder.onnx', 'encoder.onnx.data', 'decoder.onnx', 'decoder.onnx.data',
              'joint.onnx', 'joint.onnx.data', 'genai_config.json')
$missing = @()
foreach ($f in $required) {
    $p = Join-Path $cpuModelDir $f
    if (!(Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $f }
}

if ($missing.Count -gt 0) {
    Write-Err "Missing or empty files: $($missing -join ', ')"
    Write-Err "Try running again with -Force to re-download."
    exit 1
}

Write-OK "All model files present."

# ─── Step 4: Launch ──────────────────────────────────────────────────────────

if ($SkipLaunch) {
    Write-Host ""
    Write-OK "Setup complete. Run Hush.App.exe to start."
} else {
    Write-Step "Launching Hush..."
    Start-Process $HushExe
    Write-Host ""
    Write-Host "  Hush is starting - look for the tray icon (bottom-right)." -ForegroundColor Green
    Write-Host "  Hold Ctrl+Shift+H and speak to dictate." -ForegroundColor Cyan
    Write-Host "  Right-click the tray icon for Settings or Quit." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  NOTE: First launch takes 30-60 seconds while the model loads." -ForegroundColor Yellow
    Write-Host "        A small overlay will appear at the bottom of your screen." -ForegroundColor Yellow
}

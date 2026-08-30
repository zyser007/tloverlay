<#
.SYNOPSIS
    Downloads the llama.cpp server and a GGUF translation model into runtime/.

.DESCRIPTION
    TLOverlay translates locally, so it needs two things that are too large to
    keep in git: a llama.cpp server binary and a quantised model. Both land in
    runtime/ and models/, which are gitignored.

    Nothing here runs at app startup - fetch once during setup, then the app is
    fully offline.

.PARAMETER ModelUrl
    Direct URL to a GGUF file. Defaults to a Thai-tuned 4B instruct model, which
    produces markedly more natural Thai than a general model of the same size.

.PARAMETER LlamaAsset
    Which llama.cpp release asset to take. Use a cuda build if you intend to
    offload layers to the GPU.

.EXAMPLE
    pwsh tools/fetch-models.ps1
#>
[CmdletBinding()]
param(
    [string]$ModelUrl = 'https://huggingface.co/scb10x/llama3.2-typhoon2-3b-instruct-gguf/resolve/main/llama3.2-typhoon2-3b-instruct-q4_k_m.gguf',
    [string]$ModelFileName = 'translator.gguf',
    [string]$LlamaAsset = 'llama-*-bin-win-cpu-x64.zip',
    [string]$LlamaTag = 'latest'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $root 'runtime'
$modelsDir = Join-Path $root 'models'

New-Item -ItemType Directory -Force -Path $runtimeDir, $modelsDir | Out-Null

function Get-LlamaReleaseAsset {
    param([string]$Tag, [string]$Pattern)

    $uri = if ($Tag -eq 'latest') {
        'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest'
    } else {
        "https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/$Tag"
    }

    Write-Host "Querying llama.cpp release ($Tag)..."
    $release = Invoke-RestMethod -Uri $uri -Headers @{ 'User-Agent' = 'tloverlay-setup' }

    $asset = $release.assets | Where-Object { $_.name -like $Pattern } | Select-Object -First 1
    if (-not $asset) {
        $available = ($release.assets | ForEach-Object { $_.name }) -join "`n  "
        throw "No asset matching '$Pattern' in release $($release.tag_name). Available:`n  $available"
    }

    return $asset
}

$serverExe = Join-Path $runtimeDir 'llama-server.exe'

if (Test-Path $serverExe) {
    Write-Host "llama-server.exe already present, skipping."
} else {
    $asset = Get-LlamaReleaseAsset -Tag $LlamaTag -Pattern $LlamaAsset
    $zipPath = Join-Path $env:TEMP $asset.name

    Write-Host "Downloading $($asset.name) ..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

    Write-Host "Extracting to $runtimeDir ..."
    Expand-Archive -Path $zipPath -DestinationPath $runtimeDir -Force
    Remove-Item $zipPath -Force

    # Release archives nest the binaries a directory deep; flatten so the app can
    # find llama-server.exe at a predictable path.
    if (-not (Test-Path $serverExe)) {
        $found = Get-ChildItem -Path $runtimeDir -Filter 'llama-server.exe' -Recurse |
            Select-Object -First 1
        if (-not $found) {
            throw "llama-server.exe was not found in the extracted archive."
        }

        Get-ChildItem -Path $found.DirectoryName -File |
            Move-Item -Destination $runtimeDir -Force
    }
}

$modelPath = Join-Path $modelsDir $ModelFileName

if (Test-Path $modelPath) {
    Write-Host "Model already present at $modelPath, skipping."
} else {
    Write-Host "Downloading model (this is a couple of GB) ..."
    Invoke-WebRequest -Uri $ModelUrl -OutFile $modelPath
}

Write-Host ""
Write-Host "Done."
Write-Host "  server: $serverExe"
Write-Host "  model:  $modelPath"
Write-Host ""
Write-Host "Check the model's licence before redistributing - see NOTICE.md."

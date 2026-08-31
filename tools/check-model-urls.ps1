<#
.SYNOPSIS
    Verifies that every model URL in ModelCatalog.cs still resolves.

.DESCRIPTION
    A catalog entry that has moved or been withdrawn upstream does not fail here
    - it fails on a user's machine, after they press download. That happened
    once with URLs that were never checked in the first place, so CI checks them
    on every push.

    Definite rejections (401, 403, 404, 410) fail the build. Timeouts and 5xx are
    reported but tolerated, so an upstream hiccup does not block unrelated work.
#>
[CmdletBinding()]
param(
    [string]$CatalogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/TLOverlay.Core/Setup/ModelCatalog.cs'),
    [int]$Attempts = 3
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not (Test-Path $CatalogPath)) {
    throw "Catalog not found at $CatalogPath"
}

$urls = Select-String -Path $CatalogPath -Pattern 'https://[^\s"]+\.gguf' -AllMatches |
    ForEach-Object { $_.Matches } |
    ForEach-Object { $_.Value } |
    Select-Object -Unique

if (-not $urls) {
    throw "No model URLs found in $CatalogPath - has the file moved or changed shape?"
}

Write-Host "Checking $($urls.Count) model URL(s)."

$broken = @()

foreach ($url in $urls) {
    $status = $null
    $length = 0

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            # HEAD, so this never pulls gigabytes onto the runner.
            $response = Invoke-WebRequest -Uri $url -Method Head -MaximumRedirection 10 -TimeoutSec 45
            $status = [int]$response.StatusCode
            $length = [int64]($response.Headers['Content-Length'] | Select-Object -First 1)
            break
        } catch {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null }

            # A definite rejection will not change on a retry.
            if ($status -in 401, 403, 404, 410) { break }

            if ($attempt -lt $Attempts) { Start-Sleep -Seconds (2 * $attempt) }
        }
    }

    if ($status -eq 200) {
        $gb = [math]::Round($length / 1GB, 2)
        Write-Host "  OK    $gb GB  $url"
    } elseif ($status -in 401, 403, 404, 410) {
        Write-Host "  BROKEN ($status)  $url"
        $broken += "$status  $url"
    } else {
        Write-Warning "  UNVERIFIED ($($status ?? 'no response'))  $url"
    }
}

if ($broken.Count -gt 0) {
    throw "These catalog URLs no longer resolve:`n  $($broken -join "`n  ")"
}

Write-Host "All catalog URLs resolve."

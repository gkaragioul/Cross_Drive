param(
    [string]$PfxPath = "",
    [string]$PfxPassword = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$certDir = Join-Path $root "build\certs"
New-Item -ItemType Directory -Path $certDir -Force | Out-Null

$resolvedPfx = $PfxPath
if ([string]::IsNullOrWhiteSpace($resolvedPfx)) {
    $resolvedPfx = Join-Path $certDir "crossdrive-signing-placeholder.pfx"
    if (-not (Test-Path $resolvedPfx)) {
        Set-Content -Path $resolvedPfx -Value "PLACEHOLDER - replace with a real code signing PFX for production." -Encoding ascii
    }
}

if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
    $PfxPassword = "CHANGE_ME_WITH_REAL_CERT_PASSWORD"
}

$cscLink = "file:///$($resolvedPfx -replace '\\','/')"
[string]$winCscLink = (Resolve-Path $resolvedPfx).Path
[Environment]::SetEnvironmentVariable("CSC_LINK", $cscLink, "User")
[Environment]::SetEnvironmentVariable("CSC_KEY_PASSWORD", $PfxPassword, "User")
[Environment]::SetEnvironmentVariable("WIN_CSC_LINK", $winCscLink, "User")
[Environment]::SetEnvironmentVariable("WIN_CSC_KEY_PASSWORD", $PfxPassword, "User")

Write-Host "Configured user-level signing env vars:"
Write-Host "CSC_LINK=$cscLink"
Write-Host "CSC_KEY_PASSWORD=<hidden>"
Write-Host "WIN_CSC_LINK=$winCscLink"
Write-Host "WIN_CSC_KEY_PASSWORD=<hidden>"
Write-Host ""
Write-Host "Important: replace placeholder PFX with a real certificate before commercial release."

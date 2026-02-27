$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Force unsigned build for CI/staging validation.
Remove-Item Env:CSC_LINK -ErrorAction SilentlyContinue
Remove-Item Env:CSC_KEY_PASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:WIN_CSC_LINK -ErrorAction SilentlyContinue
Remove-Item Env:WIN_CSC_KEY_PASSWORD -ErrorAction SilentlyContinue

npx electron-builder --win nsis portable --publish never
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

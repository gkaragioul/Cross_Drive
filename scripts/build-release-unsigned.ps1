$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$version = (Get-Content package.json -Raw | ConvertFrom-Json).version
foreach ($artifact in @("dist\CrossDriveSetup.exe", "dist\CrossDrive-$version.exe")) {
    if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
}

# Force unsigned build for CI/staging validation.
Remove-Item Env:CSC_LINK -ErrorAction SilentlyContinue
Remove-Item Env:CSC_KEY_PASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:WIN_CSC_LINK -ErrorAction SilentlyContinue
Remove-Item Env:WIN_CSC_KEY_PASSWORD -ErrorAction SilentlyContinue

npx electron-builder --win nsis portable --publish never --config.win.signAndEditExecutable=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

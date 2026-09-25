$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Running pre-release quality gates..."
npm run test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

npm run security:audit
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

npm run commercial:gate
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Validating production signing configuration..."
powershell -ExecutionPolicy Bypass -File (Join-Path $root "scripts\verify-signing-config.ps1") -RequireRealCert
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Packaging the exact GPL source for this release..."
npm run license:source-bundle -- --release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$version = (Get-Content package.json -Raw | ConvertFrom-Json).version
foreach ($artifact in @("dist\CrossDriveSetup.exe", "dist\CrossDrive-$version.exe")) {
    if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
}

Write-Host "Building signed release artifacts..."
npm run release:win:full
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running strict signed release audit..."
npm run release:audit
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Release candidate passed all gates."

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$distDir = Join-Path $root "dist"

$candidates = @(
    Get-ChildItem -LiteralPath $distDir -Filter "MacMount *.exe" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*Setup*" } |
        Sort-Object LastWriteTime -Descending,
    Get-ChildItem -LiteralPath $distDir -Filter "MacMount Setup *.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
) | Select-Object -First 1

if (-not $candidates) {
    throw "No MacMount executable found under $distDir. Build a release first with 'npm run release:win:unsigned'."
}

$exePath = $candidates.FullName
Write-Host "Launching $exePath"

$proc = Start-Process -FilePath $exePath -PassThru -Verb RunAs
Write-Host "Started with PID: $($proc.Id)"
Start-Sleep -Seconds 5

if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) {
    Write-Host 'Process is running'
} else {
    Write-Host 'Process exited'
}

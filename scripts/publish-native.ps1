$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "native\bin"

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outDir "service") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outDir "broker") -Force | Out-Null

$serviceProj = Join-Path $root "native\MacMount.NativeService\MacMount.NativeService.csproj"
$brokerProj = Join-Path $root "native\MacMount.NativeBroker\MacMount.NativeBroker.csproj"

# Stop running native processes to avoid file locks during publish.
Get-Process -Name "MacMount.NativeBroker","MacMount.NativeService" -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
}

Write-Host "Publishing MacMount.NativeService..."
dotnet publish $serviceProj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $outDir "service")
if ($LASTEXITCODE -ne 0) { throw "NativeService publish failed with exit code $LASTEXITCODE." }

Write-Host "Publishing MacMount.NativeBroker..."
dotnet publish $brokerProj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $outDir "broker")
if ($LASTEXITCODE -ne 0) { throw "NativeBroker publish failed with exit code $LASTEXITCODE." }

$serviceExe = Join-Path $outDir "service\MacMount.NativeService.exe"
$brokerExe = Join-Path $outDir "broker\MacMount.NativeBroker.exe"

if (-not (Test-Path $serviceExe)) { throw "NativeService publish failed: $serviceExe not found." }
if (-not (Test-Path $brokerExe)) { throw "NativeBroker publish failed: $brokerExe not found." }

Write-Host "Native binaries published to $outDir"

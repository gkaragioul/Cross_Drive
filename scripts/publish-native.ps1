$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "native\bin"

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outDir "service") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outDir "broker") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outDir "user-session") -Force | Out-Null

# Avoid shipping stale helper binaries from the pre-rename output names.
foreach ($subdir in @("service", "broker", "user-session")) {
    Get-ChildItem -LiteralPath (Join-Path $outDir $subdir) -Filter "CrossDrive.*" -File -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    }
}

$serviceProj = Join-Path $root "native\CrossDrive.NativeService\CrossDrive.NativeService.csproj"
$brokerProj = Join-Path $root "native\CrossDrive.NativeBroker\CrossDrive.NativeBroker.csproj"
$userSessionProj = Join-Path $root "native\CrossDrive.UserSessionHelper\CrossDrive.UserSessionHelper.csproj"

# Stop running native processes to avoid file locks during publish.
Get-Process -Name "CrossDrive.NativeBroker","CrossDrive.NativeService","CrossDrive.UserSessionHelper","CrossDrive.NativeBroker","CrossDrive.NativeService","CrossDrive.UserSessionHelper" -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
}

Write-Host "Publishing CrossDrive.NativeService..."
dotnet publish $serviceProj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $outDir "service")
if ($LASTEXITCODE -ne 0) { throw "NativeService publish failed with exit code $LASTEXITCODE." }

Write-Host "Publishing CrossDrive.NativeBroker..."
dotnet publish $brokerProj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $outDir "broker")
if ($LASTEXITCODE -ne 0) { throw "NativeBroker publish failed with exit code $LASTEXITCODE." }

Write-Host "Publishing CrossDrive.UserSessionHelper..."
dotnet publish $userSessionProj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $outDir "user-session")
if ($LASTEXITCODE -ne 0) { throw "UserSessionHelper publish failed with exit code $LASTEXITCODE." }

$serviceExe = Join-Path $outDir "service\CrossDrive.NativeService.exe"
$brokerExe = Join-Path $outDir "broker\CrossDrive.NativeBroker.exe"
$userSessionExe = Join-Path $outDir "user-session\CrossDrive.UserSessionHelper.exe"

if (-not (Test-Path $serviceExe)) { throw "NativeService publish failed: $serviceExe not found." }
if (-not (Test-Path $brokerExe)) { throw "NativeBroker publish failed: $brokerExe not found." }
if (-not (Test-Path $userSessionExe)) { throw "UserSessionHelper publish failed: $userSessionExe not found." }

Write-Host "Native binaries published to $outDir"

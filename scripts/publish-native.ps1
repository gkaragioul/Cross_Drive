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

$bridgeSrc = Join-Path $root "native-bridge\apfs-fuse\build\apfs-fuse.exe"
$bridgeBinDir = Join-Path $root "native-bridge-bin"
$bridgeDst = Join-Path $bridgeBinDir "apfs-fuse.exe"
$apfsSrcRoot = Join-Path $root "native-bridge\apfs-fuse"
if (Test-Path -LiteralPath $bridgeSrc) {
    New-Item -ItemType Directory -Path $bridgeBinDir -Force | Out-Null
    Copy-Item -LiteralPath $bridgeSrc -Destination $bridgeDst -Force
    Write-Host "Copied APFS bridge to $bridgeDst"
    $gitHead = Join-Path $apfsSrcRoot ".git\HEAD"
    if (Test-Path -LiteralPath $gitHead) {
        $rev = (Get-Content -LiteralPath $gitHead -Raw).Trim()
        $notePath = Join-Path $bridgeBinDir "APFS_FUSE_SOURCE_REV.txt"
        @(
            "APFS-FUSE binary was copied from: $bridgeSrc",
            "Upstream checkout (.git/HEAD at publish time):",
            $rev,
            "",
            "Record this revision in release notes for GPL source correspondence."
        ) | Set-Content -LiteralPath $notePath -Encoding UTF8
        Write-Host "Wrote $notePath"
    }
} else {
    Write-Host "APFS bridge not present at $bridgeSrc (optional; clone/build apfs-fuse to enable fallback)."
}

Write-Host "Native binaries published to $outDir"

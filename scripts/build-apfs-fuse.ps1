# Build apfs-fuse.exe for Windows using MSYS2 + MinGW-w64 + WinFsp FUSE compatibility layer
# Prerequisites:
#   1. MSYS2 installed at C:\msys64
#   2. WinFsp installed (runtime + SDK headers)
#   3. Run from an elevated PowerShell prompt
#
# Usage: .\scripts\build-apfs-fuse.ps1

$ErrorActionPreference = "Stop"
$apfsFuseDir = Join-Path $PSScriptRoot "..\native-bridge\apfs-fuse"
$buildDir = Join-Path $apfsFuseDir "build"
$winfspInc = Join-Path $apfsFuseDir "winfsp-inc"
$msysGcc = "C:\msys64\usr\bin\gcc.exe"
$msysGpp = "C:\msys64\usr\bin\g++.exe"
$msysCmake = "C:\msys64\usr\bin\cmake.exe"
$msysMake = "C:\msys64\usr\bin\mingw32-make.exe"

Write-Host "[apfs-fuse] Checking prerequisites..." -ForegroundColor Cyan

if (-not (Test-Path $msysGcc)) {
    Write-Error "MSYS2 GCC not found at $msysGcc. Install MSYS2 and mingw-w64-x86_64-gcc."
    exit 1
}

if (-not (Test-Path "$winfspInc\fuse3\fuse.h")) {
    Write-Error "WinFsp FUSE headers not found at $winfspInc. Copy from WinFsp SDK."
    exit 1
}

if (-not (Test-Path $apfsFuseDir)) {
    Write-Host "[apfs-fuse] Cloning apfs-fuse source..." -ForegroundColor Yellow
    git clone https://github.com/sgan81/apfs-fuse.git $apfsFuseDir
    git -C $apfsFuseDir submodule init
    git -C $apfsFuseDir submodule update
}

if (-not (Test-Path (Join-Path $apfsFuseDir "3rdparty\lzfse\src\lzfse.h"))) {
    Write-Host "[apfs-fuse] Initializing lzfse submodule..." -ForegroundColor Yellow
    git -C $apfsFuseDir submodule update --init --recursive
}

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

Write-Host "[apfs-fuse] Configuring CMake..." -ForegroundColor Cyan
Push-Location $buildDir
try {
    $cmakeArgs = @(
        "-G", "Unix Makefiles",
        "-DCMAKE_C_COMPILER=$($msysGcc -replace '\\', '/')",
        "-DCMAKE_CXX_COMPILER=$($msysGpp -replace '\\', '/')",
        "-DCMAKE_MAKE_PROGRAM=$($msysMake -replace '\\', '/')",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DWINFSP_INC=$($winfspInc -replace '\\', '/')",
        ".."
    )
    & $msysCmake @cmakeArgs 2>&1 | Tee-Object -Variable cmakeOut
    if ($LASTEXITCODE -ne 0) {
        Write-Error "CMake configuration failed."
        exit 1
    }

    Write-Host "[apfs-fuse] Building..." -ForegroundColor Cyan
    & $msysMake -j$( [Environment]::ProcessorCount ) 2>&1 | Tee-Object -Variable makeOut
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed."
        exit 1
    }

    $exePath = Join-Path $buildDir "apfs-fuse.exe"
    if (Test-Path $exePath) {
        $destDir = Join-Path $PSScriptRoot "..\native-bridge-bin"
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        Copy-Item $exePath (Join-Path $destDir "apfs-fuse.exe") -Force
        Write-Host "[apfs-fuse] SUCCESS: Built and copied to native-bridge-bin\apfs-fuse.exe" -ForegroundColor Green
    } else {
        Write-Error "Build completed but apfs-fuse.exe not found."
        exit 1
    }
} finally {
    Pop-Location
}

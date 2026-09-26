param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $checks.Add([pscustomobject]@{
        Check  = $Name
        Passed = $Passed
        Detail = $Detail
    }) | Out-Null
}

function Require-File {
    param(
        [string]$Name,
        [string]$Path,
        [int64]$MinBytes = 1
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check $Name $false "Missing: $Path"
        return
    }

    $item = Get-Item -LiteralPath $Path
    Add-Check $Name ($item.Length -ge $MinBytes) "$Path ($($item.Length) bytes)"
}

function Require-Directory {
    param(
        [string]$Name,
        [string]$Path
    )

    Add-Check $Name (Test-Path -LiteralPath $Path -PathType Container) $Path
}

$dist = Join-Path $Root "dist"
$unpacked = Join-Path $dist "win-unpacked"
$resources = Join-Path $unpacked "resources"
$nativeBin = Join-Path $resources "native-bin"

Write-Host "CrossDrive installer smoke check"
Write-Host "================================"
Write-Host "Root: $Root"

Require-File "NSIS installer exists" (Join-Path $dist "CrossDriveSetup.exe") 1000000
Require-File "Portable artifact exists" (Join-Path $dist "CrossDrive-1.5.34.exe") 1000000
Require-File "Unpacked app executable exists" (Join-Path $unpacked "CrossDrive.exe") 1000000
Require-File "app.asar exists" (Join-Path $resources "app.asar") 100000
Require-Directory "native-bin resource directory exists" $nativeBin
Require-File "Bundled WinFsp MSI exists" (Join-Path $resources "prereqs\winfsp.msi") 1000000
Require-File "Runtime setup helper exists" (Join-Path $resources "scripts\CrossDrive.ps1") 1000
Require-File "User-session mapping helper exists" (Join-Path $resources "scripts\map-drive-user-session.ps1") 1000

$nativeHelpers = @(
    "service\CrossDrive.NativeService.exe",
    "broker\CrossDrive.NativeBroker.exe",
    "user-session\CrossDrive.UserSessionHelper.exe"
)

foreach ($helper in $nativeHelpers) {
    Require-File "Packaged native helper: $helper" (Join-Path $nativeBin $helper) 100000
}

$forbiddenPaths = @(
    "native-bridge-bin",
    "prereqs\crossdrive-kernel",
    "scripts\wslMountClient.js",
    "scripts\wslSetup.js",
    "scripts\wsl_mount.sh",
    "scripts\wsl_unmount.sh",
    "scripts\wsl_install_modules.sh",
    "scripts\wsl_validate_mount.sh",
    "scripts\mount_drive.sh",
    "scripts\setup_linux.sh",
    "apfs-fuse.exe",
    "dotnet.exe",
    "wsl.exe"
)

foreach ($relative in $forbiddenPaths) {
    $found = @(Get-ChildItem -LiteralPath $resources -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName.EndsWith($relative, [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.Name.Equals($relative, [System.StringComparison]::OrdinalIgnoreCase)
        })
    Add-Check "Retired payload absent: $relative" ($found.Count -eq 0) ($(if ($found.Count -eq 0) { "Absent" } else { ($found | Select-Object -First 5 -ExpandProperty FullName) -join "; " }))
}

$setupHelper = Join-Path $resources "scripts\CrossDrive.ps1"
if (Test-Path -LiteralPath $setupHelper -PathType Leaf) {
    $setupText = Get-Content -LiteralPath $setupHelper -Raw
    Add-Check "Runtime setup uses bundled WinFsp MSI" ($setupText -match "winfsp\.msi" -and $setupText -match "msiexec") $setupHelper
    Add-Check "Runtime setup avoids package managers/download bootstrap" ($setupText -notmatch "winget|choco|Invoke-WebRequest|Start-BitsTransfer|wsl\.exe") $setupHelper
}

$failed = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize

if ($failed.Count -gt 0) {
    Write-Error "Installer smoke check FAILED ($($failed.Count) failing checks)."
    exit 1
}

Write-Host ""
Write-Host "Installer smoke check PASSED."

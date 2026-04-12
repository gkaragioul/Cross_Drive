# MacMount End-to-End Validation Script
# Run as Administrator on a clean Windows machine with a Mac drive attached.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\validate-release.ps1

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$passCount = 0
$failCount = 0
$warnCount = 0

function Pass($msg) {
    $script:passCount++
    Write-Host "[PASS] $msg" -ForegroundColor Green
}

function Fail($msg) {
    $script:failCount++
    Write-Host "[FAIL] $msg" -ForegroundColor Red
}

function Warn($msg) {
    $script:warnCount++
    Write-Host "[WARN] $msg" -ForegroundColor Yellow
}

function Section($msg) {
    Write-Host "`n=== $msg ===" -ForegroundColor Cyan
}

# ============================================================
Section "1. Prerequisite Checks"
# ============================================================

# WinFsp runtime
$winFspSvc = Get-Service -Name "WinFsp.Launcher" -ErrorAction SilentlyContinue
if ($winFspSvc) {
    Pass "WinFsp.Launcher service is installed and running"
} else {
    $winFspPath = Test-Path "C:\Program Files (x86)\WinFsp\bin\launchctl-x64.exe"
    if ($winFspPath) {
        Pass "WinFsp binaries found (service may not be started yet)"
    } else {
        Fail "WinFsp is not installed. Run the installer or use the Auto-Install button."
    }
}

# fsptool
$fsptool = "C:\Program Files (x86)\WinFsp\bin\fsptool-x64.exe"
if (Test-Path $fsptool) {
    Pass "fsptool-x64.exe found at $fsptool"
} else {
    Fail "fsptool-x64.exe not found"
}

# .NET runtime
try {
    $dotnetVer = dotnet --version 2>$null
    if ($dotnetVer) {
        Pass ".NET runtime available: $dotnetVer"
    } else {
        Fail ".NET runtime not found"
    }
} catch {
    Fail ".NET runtime check failed: $_"
}

# Node.js
try {
    $nodeVer = node --version 2>$null
    if ($nodeVer) {
        Pass "Node.js available: $nodeVer"
    } else {
        Fail "Node.js not found"
    }
} catch {
    Fail "Node.js check failed: $_"
}

# Admin rights
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Pass "Running as Administrator"
} else {
    Fail "Not running as Administrator (required for raw disk access)"
}

# ============================================================
Section "2. Bundled Resources"
# ============================================================

$scriptDir = Split-Path $PSScriptRoot -Parent

# WinFsp MSI
$msiPath = Join-Path $scriptDir "prereqs\winfsp.msi"
if (Test-Path $msiPath) {
    $msiSize = (Get-Item $msiPath).Length
    Pass "winfsp.msi bundled ($([math]::Round($msiSize/1MB, 1)) MB)"
} else {
    Fail "winfsp.msi not found in prereqs/"
}

# Native binaries
$nativeBinDir = Join-Path $scriptDir "native\bin"
if (Test-Path $nativeBinDir) {
    $dllCount = (Get-ChildItem $nativeBinDir -Recurse -Filter "*.dll" -ErrorAction SilentlyContinue).Count
    if ($dllCount -gt 0) {
        Pass "Native binaries built ($dllCount DLLs)"
    } else {
        Warn "native/bin/ exists but contains no DLLs"
    }
} else {
    Fail "native/bin/ directory not found. Run: npm run native:publish"
}

# apfs-fuse.exe (optional but recommended)
$apfsFuseCandidates = @(
    (Join-Path $scriptDir "native-bridge-bin\apfs-fuse.exe"),
    (Join-Path $scriptDir "native-bridge\apfs-fuse\build\apfs-fuse.exe")
)
$apfsFuseFound = $false
foreach ($c in $apfsFuseCandidates) {
    if (Test-Path $c) {
        Pass "apfs-fuse.exe found at $c"
        $apfsFuseFound = $true
        break
    }
}
if (-not $apfsFuseFound) {
    Warn "apfs-fuse.exe not found. Encrypted APFS volumes will not be unlockable. Run: scripts\build-apfs-fuse.ps1"
}

# PowerShell scripts
$psScripts = @("MacMount.ps1", "map-drive-user-session.ps1")
foreach ($ps in $psScripts) {
    $psPath = Join-Path $scriptDir "scripts\$ps"
    if (Test-Path $psPath) {
        Pass "$ps found"
    } else {
        Fail "$ps not found"
    }
}

# ============================================================
Section "3. PowerShell Script Validation"
# ============================================================

$psScript = Join-Path $scriptDir "scripts\MacMount.ps1"

# Preflight check
try {
    $preflightRaw = & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $psScript -Action "PreflightCheck" 2>$null
    $preflight = $preflightRaw | ConvertFrom-Json
    if ($preflight.success) {
        Pass "PreflightCheck endpoint works"
        foreach ($item in $preflight.items) {
            if ($item.ok) {
                Pass "  - $($item.title): $($item.detail)"
            } else {
                Warn "  - $($item.title): $($item.detail)"
            }
        }
    } else {
        Fail "PreflightCheck returned success=false"
    }
} catch {
    Fail "PreflightCheck failed to execute: $_"
}

# Drive listing
try {
    $drivesRaw = & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $psScript -Action "List" 2>$null
    $drives = $drivesRaw | ConvertFrom-Json
    if ($drives -is [Array]) {
        $macDrives = $drives | Where-Object { $_.isMac -eq $true }
        Pass "Drive enumeration works: $($drives.Count) disk(s) found, $($macDrives.Count) Mac-formatted"
        foreach ($d in $macDrives) {
            Write-Host "  - Disk $($d.id): $($d.name) ($($d.format)) - $(if($d.mounted){'MOUNTED'}else{'unmounted'})" -ForegroundColor White
        }
    } else {
        Warn "Drive list returned unexpected format"
    }
} catch {
    Fail "Drive listing failed: $_"
}

# ============================================================
Section "4. API Endpoint Checks"
# ============================================================

# Check if Express server is running
try {
    $statusRes = Invoke-WebRequest -Uri "http://localhost:3001/api/status" -UseBasicParsing -TimeoutSec 5
    $status = $statusRes.Content | ConvertFrom-Json
    Pass "Express server responding at :3001"
} catch {
    Warn "Express server not running (start with: npm run start)"
}

# ============================================================
Section "5. Summary"
# ============================================================

Write-Host "`n" -NoNewline
Write-Host "Results: " -NoNewline -ForegroundColor White
Write-Host "$passCount passed" -NoNewline -ForegroundColor Green
Write-Host ", " -NoNewline -ForegroundColor White
Write-Host "$warnCount warnings" -NoNewline -ForegroundColor Yellow
Write-Host ", " -NoNewline -ForegroundColor White
Write-Host "$failCount failed" -ForegroundColor Red

if ($failCount -eq 0) {
    Write-Host "`nMacMount is ready for release testing." -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n$failCount issue(s) must be resolved before release." -ForegroundColor Red
    exit 1
}

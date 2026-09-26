param(
    [string]$InstallerPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist\CrossDriveSetup.exe"),
    [string]$OutputPath = (Join-Path $env:TEMP "crossdrive-clean-install-evidence.json"),
    [switch]$SkipInstall,
    [switch]$ConfirmCleanMachine
)

$ErrorActionPreference = "Stop"

$legacyRelativePaths = @(
    "resources\native-bridge-bin",
    "resources\prereqs\crossdrive-kernel",
    "resources\scripts\wslMountClient.js",
    "resources\scripts\wslSetup.js",
    "resources\scripts\setup_linux.sh",
    "resources\scripts\mount_drive.sh",
    "resources\apfs-fuse.exe",
    "resources\dotnet.exe",
    "resources\wsl.exe"
)

function Get-SignatureStatus([string]$Path) {
    if (-not (Test-Path $Path)) { return "Missing" }
    try {
        return [string](Get-AuthenticodeSignature -FilePath $Path).Status
    } catch {
        return "Unavailable"
    }
}

function Find-InstallDir {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} "CrossDrive"),
        (Join-Path ${env:ProgramFiles(x86)} "CrossDrive")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "CrossDrive.exe")) {
            return $candidate
        }
    }

    $uninstallRoots = @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($root in $uninstallRoots) {
        $entry = Get-ItemProperty $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -eq "CrossDrive" -and $_.InstallLocation } |
            Select-Object -First 1
        if ($entry -and (Test-Path (Join-Path $entry.InstallLocation "CrossDrive.exe"))) {
            return $entry.InstallLocation
        }
    }

    return $null
}

function Test-AppLaunch([string]$ExePath) {
    if (-not (Test-Path $ExePath)) { return $false }
    $proc = $null
    try {
        $proc = Start-Process -FilePath $ExePath -ArgumentList "--production-smoke" -PassThru -WindowStyle Minimized
        Start-Sleep -Seconds 8
        return -not $proc.HasExited
    } catch {
        return $false
    } finally {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not $ConfirmCleanMachine) {
    throw "Clean-install smoke must be run on a fresh Windows validation machine. Re-run with -ConfirmCleanMachine after confirming the machine has no prior CrossDrive install."
}

if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

$installerSignatureStatus = Get-SignatureStatus $InstallerPath

if (-not $SkipInstall) {
    $process = Start-Process -FilePath $InstallerPath -ArgumentList "/S" -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "Installer exited with code $($process.ExitCode)."
    }
}

$installDir = Find-InstallDir
$appExe = if ($installDir) { Join-Path $installDir "CrossDrive.exe" } else { $null }
$resourceDir = if ($installDir) { Join-Path $installDir "resources" } else { $null }
$nativeBinDir = if ($resourceDir) { Join-Path $resourceDir "native-bin" } else { $null }

$nativeHelpers = @(
    "service\CrossDrive.NativeService.exe",
    "broker\CrossDrive.NativeBroker.exe",
    "user-session\CrossDrive.UserSessionHelper.exe"
)
$nativeHelpersPresent = $true
foreach ($helper in $nativeHelpers) {
    if (-not (Test-Path (Join-Path $nativeBinDir $helper))) {
        $nativeHelpersPresent = $false
    }
}

$legacyPayloadsAbsent = $true
foreach ($legacy in $legacyRelativePaths) {
    if ($installDir -and (Test-Path (Join-Path $installDir $legacy))) {
        $legacyPayloadsAbsent = $false
    }
}

$winFspInstalled =
    $null -ne (Get-Service -Name "WinFsp.Launcher" -ErrorAction SilentlyContinue) -or
    (Test-Path "C:\Program Files (x86)\WinFsp\bin\launchctl-x64.exe")

$installedAppLaunched = if ($appExe) { Test-AppLaunch $appExe } else { $false }
$installedAppExists = [bool]($appExe -and (Test-Path $appExe))
$bundledDotnetAbsent = if ($installDir) { -not (Test-Path (Join-Path $installDir "resources\dotnet.exe")) } else { $false }
$bundledWslAbsent = if ($installDir) { -not (Test-Path (Join-Path $installDir "resources\wsl.exe")) } else { $false }
$noExternalRuntimeRequired =
    $nativeHelpersPresent -and
    $legacyPayloadsAbsent -and
    $bundledDotnetAbsent -and
    $bundledWslAbsent

$complete =
    $installerSignatureStatus -eq "Valid" -and
    $installDir -and
    $installedAppExists -and
    $nativeHelpersPresent -and
    $winFspInstalled -and
    $installedAppLaunched -and
    $legacyPayloadsAbsent -and
    $noExternalRuntimeRequired

$payload = [pscustomobject]@{
    generatedBy = "CrossDrive clean-install-smoke.ps1"
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    scenario = "clean-windows-install"
    cleanMachineConfirmed = [bool]$ConfirmCleanMachine
    complete = [bool]$complete
    installerPath = (Resolve-Path $InstallerPath).Path
    installerSignatureStatus = $installerSignatureStatus
    installDir = $installDir
    installedAppExists = [bool]$installedAppExists
    installedAppLaunched = [bool]$installedAppLaunched
    winFspInstalled = [bool]$winFspInstalled
    nativeHelpersPresent = [bool]$nativeHelpersPresent
    legacyPayloadsAbsent = [bool]$legacyPayloadsAbsent
    noExternalRuntimeRequired = [bool]$noExternalRuntimeRequired
}

$outDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$payload | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -Path $OutputPath

Write-Host "Clean-install smoke evidence written to $OutputPath"
if (-not $complete) {
    Write-Host "Clean-install smoke FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "Clean-install smoke PASSED." -ForegroundColor Green

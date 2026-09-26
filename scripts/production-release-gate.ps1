param(
    [string]$RealMediaEvidencePath = $env:CROSSDRIVE_REAL_MEDIA_EVIDENCE,
    [string]$CleanInstallEvidencePath = $env:CROSSDRIVE_CLEAN_INSTALL_EVIDENCE
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root "dist"
$pkg = Get-Content (Join-Path $root "package.json") -Raw | ConvertFrom-Json
$setupExe = Join-Path $distDir "CrossDriveSetup.exe"
$portableExe = Join-Path $distDir "CrossDrive-$($pkg.version).exe"

$checks = New-Object System.Collections.Generic.List[object]

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:checks.Add([pscustomobject]@{
        Check = $Name
        Passed = $Passed
        Detail = $Detail
    })
}

function Test-ValidSignature([string]$Path) {
    if (-not (Test-Path $Path)) {
        return @{ Ok = $false; Detail = "Missing: $Path" }
    }

    try {
        $sig = Get-AuthenticodeSignature -FilePath $Path
        return @{
            Ok = ($sig.Status -eq "Valid")
            Detail = "Status=$($sig.Status); Subject=$($sig.SignerCertificate.Subject)"
        }
    } catch {
        return @{ Ok = $false; Detail = $_.Exception.Message }
    }
}

function Read-JsonEvidence([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Add-Check "$Name evidence path supplied" $false "Set the matching CROSSDRIVE_*_EVIDENCE environment variable or pass the script parameter."
        return $null
    }
    if (-not (Test-Path $Path)) {
        Add-Check "$Name evidence file exists" $false $Path
        return $null
    }

    try {
        Add-Check "$Name evidence file exists" $true $Path
        return Get-Content $Path -Raw | ConvertFrom-Json
    } catch {
        Add-Check "$Name evidence parses as JSON" $false $_.Exception.Message
        return $null
    }
}

function Get-CoverageStatus($Evidence, [string]$Format) {
    if ($null -eq $Evidence -or $null -eq $Evidence.coverage) { return "" }
    $entry = $Evidence.coverage.PSObject.Properties[$Format]
    if ($null -eq $entry) { return "" }
    return [string]$entry.Value.status
}

Write-Host "CrossDrive Production Release Gate"
Write-Host "================================="

Write-Host "Checking production signing configuration..."
try {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "scripts\verify-signing-config.ps1") -RequireRealCert
    Add-Check "Real signing certificate configured" ($LASTEXITCODE -eq 0) "verify-signing-config.ps1 -RequireRealCert exit=$LASTEXITCODE"
} catch {
    Add-Check "Real signing certificate configured" $false $_.Exception.Message
}

foreach ($artifact in @($setupExe, $portableExe)) {
    $name = Split-Path $artifact -Leaf
    $sig = Test-ValidSignature $artifact
    Add-Check "$name Authenticode signature is valid" $sig.Ok $sig.Detail
}

Write-Host "Checking real-media validation evidence..."
$realMedia = Read-JsonEvidence $RealMediaEvidencePath "Real-media"
if ($realMedia) {
    $supportedFormats = @("APFS", "Encrypted APFS", "HFS+", "Classic HFS")
    foreach ($format in $supportedFormats) {
        $status = Get-CoverageStatus $realMedia $format
        Add-Check "Real-media $format opened" ($status -eq "opened") "status=$status"
    }

    $coreStorageStatus = Get-CoverageStatus $realMedia "CoreStorage"
    Add-Check "Real-media CoreStorage unsupported policy verified" ($coreStorageStatus -eq "unsupported") "status=$coreStorageStatus"
    Add-Check "Real-media validation reports complete" ($realMedia.complete -eq $true) "complete=$($realMedia.complete)"
    Add-Check "Real-media validation used mount smoke" ($realMedia.mountSmokeEnabled -eq $true) "mountSmokeEnabled=$($realMedia.mountSmokeEnabled)"
}

Write-Host "Checking clean-install validation evidence..."
$cleanInstall = Read-JsonEvidence $CleanInstallEvidencePath "Clean-install"
if ($cleanInstall) {
    Add-Check "Clean-install scenario is fresh Windows install" ([string]$cleanInstall.scenario -eq "clean-windows-install") "scenario=$($cleanInstall.scenario)"
    Add-Check "Clean-install evidence complete" ($cleanInstall.complete -eq $true) "complete=$($cleanInstall.complete)"
    Add-Check "Clean-install installer signature valid" ([string]$cleanInstall.installerSignatureStatus -eq "Valid") "installerSignatureStatus=$($cleanInstall.installerSignatureStatus)"
    Add-Check "Clean-install app launched" ($cleanInstall.installedAppLaunched -eq $true) "installedAppLaunched=$($cleanInstall.installedAppLaunched)"
    Add-Check "Clean-install WinFsp installed from bundled prereq" ($cleanInstall.winFspInstalled -eq $true) "winFspInstalled=$($cleanInstall.winFspInstalled)"
    Add-Check "Clean-install legacy payloads absent" ($cleanInstall.legacyPayloadsAbsent -eq $true) "legacyPayloadsAbsent=$($cleanInstall.legacyPayloadsAbsent)"
    Add-Check "Clean-install no external runtime required" ($cleanInstall.noExternalRuntimeRequired -eq $true) "noExternalRuntimeRequired=$($cleanInstall.noExternalRuntimeRequired)"
}

Write-Host ""
$checks | Format-Table -AutoSize

$failed = @($checks | Where-Object { -not $_.Passed })
if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Production release gate FAILED: $($failed.Count) check(s) did not pass." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Production release gate PASSED." -ForegroundColor Green

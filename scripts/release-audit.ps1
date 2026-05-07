param(
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$pkgPath = Join-Path $root "package.json"
$distDir = Join-Path $root "dist"

if (-not (Test-Path $pkgPath)) { throw "package.json not found." }
$pkg = Get-Content $pkgPath | ConvertFrom-Json
$productName = if (-not [string]::IsNullOrWhiteSpace($pkg.build.productName)) { $pkg.build.productName } else { $pkg.productName }
if ([string]::IsNullOrWhiteSpace($productName)) { $productName = "GKMacOpener" }

$setupPatterns = @(
    "$productName-Setup-*.exe",
    "$productName Setup *.exe"
)
$setupExe = $null
foreach ($pattern in $setupPatterns) {
    $candidate = Get-ChildItem -Path $distDir -Filter $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($candidate) {
        $setupExe = $candidate
        break
    }
}
$portableExe = Get-ChildItem -Path $distDir -Filter "$productName *.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "$productName Setup *" -and $_.Name -notlike "$productName-Setup-*" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$releaseExe = if ($setupExe) { $setupExe } else { $portableExe }

Write-Host "$productName Release Audit"
Write-Host ("=" * ("$productName Release Audit").Length)

$checks = @()

function Get-SignatureStatus([string]$filePath) {
    # Use Windows PowerShell 5.1 first, where Authenticode cmdlets are most reliable.
    try {
        $escaped = $filePath.Replace("'", "''")
        $ps = "try { Import-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue; `$s = Get-AuthenticodeSignature -FilePath '$escaped'; Write-Output ('STATUS=' + `$s.Status) } catch { Write-Output ('ERR=' + `$_.Exception.Message) }"
        $out = powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $ps 2>$null
        $txt = ($out | Out-String).Trim()
        if ($txt -match "^STATUS=(.+)$") {
            $status = $Matches[1].Trim()
            return @{
                Ok = ($status -eq "Valid")
                Detail = "Status=$status"
            }
        }
        if ($txt -match "^ERR=(.+)$") {
            return @{
                Ok = $false
                Detail = $Matches[1].Trim()
            }
        }
    } catch {
        # continue to host/signtool fallbacks
    }

    # Fallback to current host.
    try {
        $cmd = Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue
        if ($cmd) {
            $sig = Get-AuthenticodeSignature -FilePath $filePath
            return @{
                Ok = ($sig.Status -eq "Valid")
                Detail = "Status=$($sig.Status)"
            }
        }
    } catch {
        # continue to final fallback
    }

    # Final fallback: signtool verify (if present).
    try {
        $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Path
        if ($signtool) {
            & $signtool verify /pa /v $filePath | Out-Null
            if ($LASTEXITCODE -eq 0) {
                return @{ Ok = $true; Detail = "signtool verify: valid" }
            }
            return @{ Ok = $false; Detail = "signtool verify failed (exit $LASTEXITCODE)" }
        }
    } catch {
        # ignore
    }

    return @{ Ok = $false; Detail = "Signature verification unavailable in this shell environment." }
}

$checks += [pscustomobject]@{
    Check = "Package metadata (description/author/license)"
    Passed = (-not [string]::IsNullOrWhiteSpace($pkg.description) -and -not [string]::IsNullOrWhiteSpace($pkg.author) -and -not [string]::IsNullOrWhiteSpace($pkg.license))
    Detail = "$($pkg.description) | $($pkg.author) | $($pkg.license)"
}

$checks += [pscustomobject]@{
    Check = "Release artifact produced"
    Passed = $null -ne $releaseExe
    Detail = $(if ($releaseExe) { $releaseExe.FullName } else { "No $productName executable in dist" })
}

$checks += [pscustomobject]@{
    Check = "Installer artifact produced (NSIS)"
    Passed = $null -ne $setupExe
    Detail = $(if ($setupExe) { $setupExe.FullName } else { "No $productName setup executable in dist" })
}

$checks += [pscustomobject]@{
    Check = "Portable artifact produced"
    Passed = $null -ne $portableExe
    Detail = $(if ($portableExe) { $portableExe.FullName } else { "No $productName portable executable in dist" })
}

$checks += [pscustomobject]@{
    Check = "EULA file present"
    Passed = (Test-Path (Join-Path $root "build\EULA.txt"))
    Detail = (Join-Path $root "build\EULA.txt")
}

$checks += [pscustomobject]@{
    Check = "Offline WinFsp prereq bundled"
    Passed = (Test-Path (Join-Path $root "prereqs\winfsp.msi")) -or (Test-Path (Join-Path $root "prereqs\WinFsp.msi"))
    Detail = (Join-Path $root "prereqs")
}

$packedWinFspExtract = Join-Path $root "dist\win-unpacked\resources\prereqs\winfsp-extract"
$checks += [pscustomobject]@{
    Check = "Extracted WinFsp payload not packaged"
    Passed = -not (Test-Path $packedWinFspExtract)
    Detail = $packedWinFspExtract
}

$fullPrereqsPacked = $false
try {
    $fullPrereqsPacked = @($pkg.build.extraResources | Where-Object { $_.from -eq "prereqs" }).Count -gt 0 -or
                         @($pkg.build.files | Where-Object { $_ -eq "prereqs/**/*" }).Count -gt 0 -or
                         @($pkg.build.asarUnpack | Where-Object { $_ -eq "prereqs/**/*" }).Count -gt 0
} catch {
    $fullPrereqsPacked = $true
}
$checks += [pscustomobject]@{
    Check = "Packaging avoids whole prereqs directory"
    Passed = -not $fullPrereqsPacked
    Detail = "package.json build.files/asarUnpack/extraResources"
}

$devScriptGlobsPacked = $false
try {
    $devScriptGlobsPacked = @($pkg.build.files | Where-Object {
        $_ -in @("scripts/**/*.js", "scripts/**/*.ps1", "scripts/**/*.sh")
    }).Count -gt 0 -or
    @($pkg.build.asarUnpack | Where-Object {
        $_ -in @("scripts/**/*.js", "scripts/**/*.ps1", "scripts/**/*.sh")
    }).Count -gt 0
} catch {
    $devScriptGlobsPacked = $true
}
$checks += [pscustomobject]@{
    Check = "Packaging avoids dev script globs"
    Passed = -not $devScriptGlobsPacked
    Detail = "package.json build.files/asarUnpack scripts entries"
}

$forbiddenPackagedScripts = @(
    "setup-signing-env.ps1",
    "configure-real-signing.ps1",
    "verify-signing-config.ps1",
    "build-release-unsigned.ps1",
    "release-audit.ps1",
    "release-candidate.ps1",
    "security-audit.js",
    "commercial-gate.js",
    "self-test.js",
    "validate-release.ps1",
    "start-electron.js"
)
$forbiddenFound = @()
$packagedScriptRoots = @(
    (Join-Path $root "dist\win-unpacked\resources\scripts"),
    (Join-Path $root "dist\win-unpacked\resources\app.asar.unpacked\scripts")
)
foreach ($scriptRoot in $packagedScriptRoots) {
    if (-not (Test-Path $scriptRoot)) { continue }
    foreach ($scriptName in $forbiddenPackagedScripts) {
        $candidate = Join-Path $scriptRoot $scriptName
        if (Test-Path $candidate) { $forbiddenFound += $candidate }
    }
}
$checks += [pscustomobject]@{
    Check = "Dev/release scripts not packaged"
    Passed = ($forbiddenFound.Count -eq 0)
    Detail = $(if ($forbiddenFound.Count -eq 0) { "No forbidden scripts found in resources/scripts or app.asar.unpacked/scripts" } else { ($forbiddenFound -join "; ") })
}

$noticePath = Join-Path $root "build\THIRD_PARTY_NOTICES.txt"
$noticeText = if (Test-Path $noticePath) { Get-Content $noticePath -Raw } else { "" }
$checks += [pscustomobject]@{
    Check = "WinFsp FLOSS attribution documented"
    Passed = ($noticeText -match "WinFsp - Windows File System Proxy, Copyright \(C\) Bill Zissimopoulos") -and ($noticeText -match "github\.com/winfsp/winfsp")
    Detail = $noticePath
}

$checks += [pscustomobject]@{
    Check = "GKMacOpener MIT copyright documented"
    Passed = ($noticeText -match "Copyright \(c\) 2026 GKMacOpener contributors") -and ((Get-Content (Join-Path $root "LICENSE") -Raw) -match "Copyright \(c\) 2026 GKMacOpener contributors")
    Detail = "LICENSE + THIRD_PARTY_NOTICES.txt"
}

$gplManifestPath = Join-Path $root "docs\GPL_SOURCE_MANIFEST.md"
$gplManifestText = if (Test-Path $gplManifestPath) { Get-Content $gplManifestPath -Raw } else { "" }
$checks += [pscustomobject]@{
    Check = "GPL source manifest present"
    Passed = ($gplManifestText -match "linux-msft-wsl-6\.6\.87\.2") -and
             ($gplManifestText -match "linux-apfs-rw") -and
             ($gplManifestText -match "0\.3\.20") -and
             ($gplManifestText -match "kernel ``\.config``|kernel `\.config`|kernel \.config")
    Detail = $gplManifestPath
}

$kernelPath = Join-Path $root "prereqs\macmount-kernel\wsl_kernel"
$checks += [pscustomobject]@{
    Check = "Bundled WSL kernel"
    Passed = (Test-Path $kernelPath)
    Detail = $kernelPath
}

$wslModules = @(
    @{ Label = "Bundled WSL module: apfs.ko"; Name = "apfs.ko" },
    @{ Label = "Bundled WSL module: hfs.ko"; Name = "hfs.ko" },
    @{ Label = "Bundled WSL module: hfsplus.ko"; Name = "hfsplus.ko" }
)
foreach ($module in $wslModules) {
    $modulePath = Join-Path $root "prereqs\macmount-kernel\modules\$($module.Name)"
    $checks += [pscustomobject]@{
        Check = $module.Label
        Passed = (Test-Path $modulePath)
        Detail = $modulePath
    }
}

$nativeRequired = @(
    @{ Label = "Native service published"; Path = (Join-Path $root "native\bin\service\MacMount.NativeService.exe") },
    @{ Label = "Native broker published"; Path = (Join-Path $root "native\bin\broker\MacMount.NativeBroker.exe") },
    @{ Label = "User-session helper published"; Path = (Join-Path $root "native\bin\user-session\MacMount.UserSessionHelper.exe") }
)
foreach ($item in $nativeRequired) {
    $checks += [pscustomobject]@{
        Check = $item.Label
        Passed = (Test-Path $item.Path)
        Detail = $item.Path
    }
}

$checks += [pscustomobject]@{
    Check = "Commercial docs present"
    Passed = (Test-Path (Join-Path $root "docs\GO_NO_GO.md")) -and
             (Test-Path (Join-Path $root "docs\COMMERCIAL_READINESS.md")) -and
             (Test-Path (Join-Path $root "docs\RISK_REGISTER.md")) -and
             (Test-Path (Join-Path $root "docs\SUPPORT_RUNBOOK.md"))
    Detail = (Join-Path $root "docs")
}

$effectiveCscLink = $env:CSC_LINK
if ([string]::IsNullOrWhiteSpace($effectiveCscLink)) {
    $effectiveCscLink = [Environment]::GetEnvironmentVariable("CSC_LINK", "User")
}
$signEnv = -not [string]::IsNullOrWhiteSpace($effectiveCscLink)
$usesPlaceholderPfx = $false
if ($signEnv -and $effectiveCscLink -match "macmount-signing-placeholder\.pfx") {
    $usesPlaceholderPfx = $true
}
$checks += [pscustomobject]@{
    Check = "Code signing env wired (CSC_LINK)"
    Passed = ($signEnv -or $AllowUnsigned)
    Detail = $(if ($signEnv) { "Configured" } elseif ($AllowUnsigned) { "Unsigned allowed by -AllowUnsigned; CSC_LINK not required for staging audit" } else { "Missing CSC_LINK env var" })
}

$checks += [pscustomobject]@{
    Check = "Real signing certificate configured"
    Passed = (($signEnv -and -not $usesPlaceholderPfx) -or $AllowUnsigned)
    Detail = $(if (-not $signEnv) { $(if ($AllowUnsigned) { "Unsigned allowed by -AllowUnsigned; no certificate required for staging audit" } else { "No certificate configured" }) } elseif ($usesPlaceholderPfx) { $(if ($AllowUnsigned) { "Unsigned allowed by -AllowUnsigned; placeholder PFX ignored for staging audit" } else { "Placeholder PFX detected" }) } else { "Real PFX configured" })
}

$isInstallerSigned = $false
$installerSigDetail = "No release artifact to verify"
if ($releaseExe) {
    $sigCheck = Get-SignatureStatus -filePath $releaseExe.FullName
    $isInstallerSigned = [bool]$sigCheck.Ok
    $installerSigDetail = [string]$sigCheck.Detail
}
if ($AllowUnsigned) {
    $installerSigDetail = "Unsigned allowed by -AllowUnsigned. $installerSigDetail"
    $isInstallerSigned = $true
}
$checks += [pscustomobject]@{
    Check = "Installer authenticode signature valid"
    Passed = $isInstallerSigned
    Detail = $installerSigDetail
}

$checks | Format-Table -AutoSize

$failed = $checks | Where-Object { -not $_.Passed }
if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Release audit FAILED." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Release audit PASSED." -ForegroundColor Green

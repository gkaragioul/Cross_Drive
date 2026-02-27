param(
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$pkgPath = Join-Path $root "package.json"
$distDir = Join-Path $root "dist"
$setupPatterns = @(
    "MacMount-Setup-*.exe",
    "MacMount Setup *.exe"
)
$setupExe = $null
foreach ($pattern in $setupPatterns) {
    $candidate = Get-ChildItem -Path $distDir -Filter $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($candidate) {
        $setupExe = $candidate
        break
    }
}
$portableExe = Get-ChildItem -Path $distDir -Filter "MacMount *.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "MacMount Setup *" -and $_.Name -notlike "MacMount-Setup-*" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$releaseExe = if ($setupExe) { $setupExe } else { $portableExe }

Write-Host "MacMount Release Audit"
Write-Host "====================="

if (-not (Test-Path $pkgPath)) { throw "package.json not found." }
$pkg = Get-Content $pkgPath | ConvertFrom-Json

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
    Detail = $(if ($releaseExe) { $releaseExe.FullName } else { "No MacMount executable in dist" })
}

$checks += [pscustomobject]@{
    Check = "Installer artifact produced (NSIS)"
    Passed = $null -ne $setupExe
    Detail = $(if ($setupExe) { $setupExe.FullName } else { "No MacMount setup executable in dist" })
}

$checks += [pscustomobject]@{
    Check = "Portable artifact produced"
    Passed = $null -ne $portableExe
    Detail = $(if ($portableExe) { $portableExe.FullName } else { "No MacMount portable executable in dist" })
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
    Passed = $signEnv
    Detail = $(if ($signEnv) { "Configured" } else { "Missing CSC_LINK env var" })
}

$checks += [pscustomobject]@{
    Check = "Real signing certificate configured"
    Passed = ($signEnv -and -not $usesPlaceholderPfx)
    Detail = $(if (-not $signEnv) { "No certificate configured" } elseif ($usesPlaceholderPfx) { "Placeholder PFX detected" } else { "Real PFX configured" })
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

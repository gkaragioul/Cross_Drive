param(
    [switch]$RequireRealCert
)

$ErrorActionPreference = "Stop"

function Get-EffectiveEnv([string]$name) {
    $v = [Environment]::GetEnvironmentVariable($name, "Process")
    if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }
    $v = [Environment]::GetEnvironmentVariable($name, "User")
    if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }
    return [Environment]::GetEnvironmentVariable($name, "Machine")
}

$cscLink = Get-EffectiveEnv "CSC_LINK"
$winCscLink = Get-EffectiveEnv "WIN_CSC_LINK"
$keyPassword = Get-EffectiveEnv "CSC_KEY_PASSWORD"
$winKeyPassword = Get-EffectiveEnv "WIN_CSC_KEY_PASSWORD"

if ([string]::IsNullOrWhiteSpace($cscLink) -and [string]::IsNullOrWhiteSpace($winCscLink)) {
    throw "No signing env configured. Set CSC_LINK/WIN_CSC_LINK first."
}

$pfxPath = $null
if (-not [string]::IsNullOrWhiteSpace($winCscLink)) {
    $pfxPath = $winCscLink
} elseif ($cscLink -match '^file:///(.+)$') {
    $pfxPath = $Matches[1] -replace '/', '\'
}

if ([string]::IsNullOrWhiteSpace($pfxPath)) {
    throw "Unable to resolve certificate path from CSC_LINK/WIN_CSC_LINK."
}

if (-not (Test-Path $pfxPath)) {
    throw "Signing certificate not found: $pfxPath"
}

if ($pfxPath -match 'macmount-signing-placeholder\.pfx') {
    if ($RequireRealCert) {
        throw "Placeholder certificate detected. Real certificate is required."
    }
    Write-Host "WARNING: Placeholder certificate detected."
}

if ([string]::IsNullOrWhiteSpace($keyPassword) -and [string]::IsNullOrWhiteSpace($winKeyPassword)) {
    throw "No signing password env configured (CSC_KEY_PASSWORD/WIN_CSC_KEY_PASSWORD)."
}

try {
    $pwd = if (-not [string]::IsNullOrWhiteSpace($winKeyPassword)) { $winKeyPassword } else { $keyPassword }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $pwd, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
    if (-not $cert.HasPrivateKey) {
        throw "PFX loaded but does not contain private key."
    }
    Write-Host "Signing certificate loaded successfully."
    Write-Host "Subject: $($cert.Subject)"
    Write-Host "Thumbprint: $($cert.Thumbprint)"
    Write-Host "NotBefore: $($cert.NotBefore)"
    Write-Host "NotAfter: $($cert.NotAfter)"
} catch {
    throw "Signing certificate validation failed: $($_.Exception.Message)"
}

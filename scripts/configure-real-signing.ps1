param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,
    [Parameter(Mandatory = $true)]
    [string]$PfxPassword
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PfxPath)) {
    throw "PFX file not found: $PfxPath"
}

$resolved = (Resolve-Path $PfxPath).Path
$cscLink = "file:///$($resolved -replace '\\','/')"
[string]$winCscLink = $resolved

[Environment]::SetEnvironmentVariable("CSC_LINK", $cscLink, "User")
[Environment]::SetEnvironmentVariable("CSC_KEY_PASSWORD", $PfxPassword, "User")
[Environment]::SetEnvironmentVariable("WIN_CSC_LINK", $winCscLink, "User")
[Environment]::SetEnvironmentVariable("WIN_CSC_KEY_PASSWORD", $PfxPassword, "User")

Write-Host "Configured real signing certificate for current user."
Write-Host "CSC_LINK=$cscLink"
Write-Host "CSC_KEY_PASSWORD=<hidden>"
Write-Host "WIN_CSC_LINK=$winCscLink"
Write-Host "WIN_CSC_KEY_PASSWORD=<hidden>"

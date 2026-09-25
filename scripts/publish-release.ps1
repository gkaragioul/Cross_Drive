#!/usr/bin/env pwsh
# Publish a CrossDrive release to Cross_Drive on GitHub.
# Usage: .\scripts\publish-release.ps1 -Version 1.5.35 [-AllowUnsigned] [-Manual]
#   -Manual: prepare and verify the release, then print the assets for manual upload.
#   -AllowUnsigned: explicitly permit an unsigned installer and label the release.

param(
  [Parameter(Mandatory=$true)][string]$Version,
  [switch]$AllowUnsigned,
  [switch]$Manual
)

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Invalid version '$Version'. Expected X.Y.Z (e.g. 1.5.3)."
}

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
  Write-Host "=== CrossDrive Release v$Version ===" -ForegroundColor Cyan
  $targetFullName = "gkaragioul/Cross_Drive"

  # 1. Verify clean tree on main
  $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
  if ($branch -ne 'main') { throw "Not on main (currently '$branch'). Switch first." }
  $dirty = & git status --porcelain
  if ($dirty) { throw "Working tree is dirty. Commit or stash first." }

  # The GPL provenance and binaries must already be committed for this version.
  $packageVersion = (Get-Content (Join-Path $root "package.json") -Raw | ConvertFrom-Json).version
  if ($packageVersion -ne $Version) {
    throw "Requested v$Version does not match package.json v$packageVersion. Commit the versioned source and binaries first."
  }

  Write-Host "[1/4] Building and auditing a release candidate..." -ForegroundColor Yellow
  if ($AllowUnsigned) {
    & npm run release:candidate:unsigned
  } else {
    & npm run release:candidate
  }
  if ($LASTEXITCODE -ne 0) { throw "release:candidate failed; no tag or release was created" }

  # Locate the exact three artifacts and compute their hashes.
  $setupExe = Join-Path $root "dist\CrossDriveSetup.exe"
  $portableExe = Join-Path $root ("dist\CrossDrive-{0}.exe" -f $Version)
  $gplSource = Join-Path $root ("dist\CrossDrive-GPL-Source-v{0}.zip" -f $Version)
  if (-not (Test-Path $setupExe)) { throw "Missing $setupExe" }
  if (-not (Test-Path $portableExe)) { throw "Missing $portableExe" }
  if (-not (Test-Path $gplSource)) { throw "Missing $gplSource" }
  if ($AllowUnsigned) {
    foreach ($binary in @($setupExe, $portableExe)) {
      $signature = Get-AuthenticodeSignature -FilePath $binary
      if ($signature.Status -ne 'NotSigned') { throw "Expected an unsigned binary: $binary (status: $($signature.Status))" }
    }
  }

  Write-Host "[2/4] Computing artifact SHA-256 hashes..." -ForegroundColor Yellow
  $setupHash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()
  $portableHash = (Get-FileHash $portableExe -Algorithm SHA256).Hash.ToLower()
  $sourceHash = (Get-FileHash $gplSource -Algorithm SHA256).Hash.ToLower()

  $notesSource = Join-Path $root "RELEASE_NOTES.md"
  if (-not (Test-Path $notesSource)) { throw "RELEASE_NOTES.md missing. Edit it before running this script." }
  $notes = Get-Content $notesSource -Raw
  $releaseTitle = "v$Version"
  if ($AllowUnsigned) {
    $releaseTitle += " (unsigned)"
    $notes = "**Unsigned release:** The Windows executables do not have a code-signing signature. Verify their SHA-256 hashes below before installing.`n`n$notes"
  }
  $notesFinal = "$notes`n`n## SHA-256`n- CrossDriveSetup.exe: $setupHash`n- CrossDrive-$Version.exe: $portableHash`n- CrossDrive-GPL-Source-v$Version.zip: $sourceHash`n"
  $tmpNotes = Join-Path $env:TEMP "crossdrive_release_notes_$Version.md"
  Set-Content $tmpNotes -Value $notesFinal -Encoding UTF8

  if ($Manual) {
    Write-Host ""
    Write-Host "=== Ready for manual release ===" -ForegroundColor Green
    Write-Host "After reviewing the artifacts, tag v$Version and upload all three files to ${targetFullName}:"
    Write-Host "  $setupExe"
    Write-Host "  $portableExe"
    Write-Host "  $gplSource"
    Write-Host ""
    Write-Host "Release notes:"
    Write-Host "---"
    Write-Host $notesFinal
    Write-Host "---"
    return
  }

  Write-Host "[3/4] Pushing main and v$Version tag..." -ForegroundColor Yellow
  & git push origin main
  if ($LASTEXITCODE -ne 0) { throw "Could not push main" }
  & git tag "v$Version"
  if ($LASTEXITCODE -ne 0) { throw "Could not create v$Version tag" }
  & git push origin "v$Version"
  if ($LASTEXITCODE -ne 0) { throw "Could not push v$Version tag" }

  Write-Host "[4/4] Publishing GitHub release with binary and source assets..." -ForegroundColor Yellow
  & gh release view "v$Version" --repo $targetFullName *> $null
  if ($LASTEXITCODE -eq 0) {
    & gh release edit "v$Version" --repo $targetFullName --title $releaseTitle --notes-file $tmpNotes
    if ($LASTEXITCODE -ne 0) { throw "gh release edit failed" }
    & gh release upload "v$Version" --repo $targetFullName $setupExe $portableExe $gplSource --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }
  } else {
    & gh release create "v$Version" `
        --repo $targetFullName `
        --title $releaseTitle `
        --notes-file $tmpNotes `
        --verify-tag `
        $setupExe $portableExe $gplSource
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
  }

  Write-Host ""
  Write-Host "=== Release v$Version published ===" -ForegroundColor Green
  Write-Host "URL: https://github.com/$targetFullName/releases/tag/v$Version"
}
finally {
  Pop-Location
}

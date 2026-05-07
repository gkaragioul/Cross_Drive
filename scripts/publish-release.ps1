#!/usr/bin/env pwsh
# Publish a GKMacOpener release to GK_Mac_Opener_Releases on GitHub.
# Usage: .\scripts\publish-release.ps1 -Version 1.5.3 [-Manual]
#   -Manual: skip the gh release create step; print the SHA256 line for manual upload.

param(
  [Parameter(Mandatory=$true)][string]$Version,
  [switch]$Manual
)

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Invalid version '$Version'. Expected X.Y.Z (e.g. 1.5.3)."
}

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
  Write-Host "=== GKMacOpener Release v$Version ===" -ForegroundColor Cyan

  # 1. Verify clean tree on main
  $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
  if ($branch -ne 'main') { throw "Not on main (currently '$branch'). Switch first." }
  $dirty = & git status --porcelain
  if ($dirty) { throw "Working tree is dirty. Commit or stash first." }

  # 2. Bump package.json version. Use Node so key order + indentation are
  #    preserved and the file is written as UTF-8 without BOM.
  #    (PowerShell's ConvertTo-Json reorders keys, and Set-Content -Encoding
  #    UTF8 adds a BOM that Vite's PostCSS loader rejects as invalid JSON.)
  Write-Host "[1/6] Bumping package.json version..." -ForegroundColor Yellow
  $bumpScript = "const fs=require('fs');const p=JSON.parse(fs.readFileSync('package.json','utf8'));p.version='$Version';fs.writeFileSync('package.json',JSON.stringify(p,null,2)+'\n');"
  & node -e $bumpScript
  if ($LASTEXITCODE -ne 0) { throw "package.json version bump failed" }
  & git add package.json
  & git commit -m "chore(release): v$Version"

  # 3. Build + audit
  Write-Host "[2/6] Building installer + portable..." -ForegroundColor Yellow
  & npm run release:win:full
  if ($LASTEXITCODE -ne 0) { throw "release:win:full failed" }

  Write-Host "[3/6] Running release gate..." -ForegroundColor Yellow
  & npm run release:gate
  if ($LASTEXITCODE -ne 0) { throw "release:gate failed" }

  # 4. Locate artifacts and compute hash
  $setupExe = Join-Path $root "dist\GKMacOpenerSetup.exe"
  $portableExe = Join-Path $root ("dist\GKMacOpener-{0}.exe" -f $Version)
  if (-not (Test-Path $setupExe)) { throw "Missing $setupExe" }
  if (-not (Test-Path $portableExe)) { throw "Missing $portableExe" }

  Write-Host "[4/6] Computing SHA256 of installer..." -ForegroundColor Yellow
  $hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()
  Write-Host "  SHA256: $hash" -ForegroundColor Cyan

  # 5. Build release notes from RELEASE_NOTES.md + SHA256 line
  $notesSource = Join-Path $root "RELEASE_NOTES.md"
  if (-not (Test-Path $notesSource)) { throw "RELEASE_NOTES.md missing. Edit it before running this script." }
  $notes = Get-Content $notesSource -Raw
  $notesFinal = "$notes`n`nSHA256: $hash`n"
  $tmpNotes = Join-Path $env:TEMP "gkmo_release_notes_$Version.md"
  Set-Content $tmpNotes -Value $notesFinal -Encoding UTF8

  # 6. Tag and push
  Write-Host "[5/6] Tagging v$Version and pushing..." -ForegroundColor Yellow
  & git tag "v$Version"
  & git push origin main
  & git push origin "v$Version"

  if ($Manual) {
    Write-Host ""
    Write-Host "=== Manual upload ===" -ForegroundColor Green
    Write-Host "Upload these two files to a new release v$Version on georgekgr12/GK_Mac_Opener_Releases:"
    Write-Host "  $setupExe"
    Write-Host "  $portableExe"
    Write-Host ""
    Write-Host "Paste this into the release notes (replace the existing notes):"
    Write-Host "---"
    Write-Host $notesFinal
    Write-Host "---"
    Write-Host "SHA256 line is at the bottom." -ForegroundColor Cyan
    return
  }

  # 7. gh release create on the releases repo
  Write-Host "[6/6] Creating GitHub release on GK_Mac_Opener_Releases..." -ForegroundColor Yellow
  & gh release create "v$Version" `
      --repo georgekgr12/GK_Mac_Opener_Releases `
      --title "v$Version" `
      --notes-file $tmpNotes `
      $setupExe $portableExe
  if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

  Write-Host ""
  Write-Host "=== Release v$Version published ===" -ForegroundColor Green
  Write-Host "URL: https://github.com/georgekgr12/GK_Mac_Opener_Releases/releases/tag/v$Version"
}
finally {
  Pop-Location
}

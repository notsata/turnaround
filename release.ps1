# Builds the game, zips it and publishes it as a GitHub release. Every installed copy checks the latest
# release on launch and offers to update itself.
#   .\release.ps1 0.8.0
#   .\release.ps1 0.8.1 -Notes "Fixed the belt loader achieving orbit"
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.2.10f1\Editor\Unity.exe'
$gh = 'C:\Program Files\GitHub CLI\gh.exe'

if (-not ($Version -as [version])) { throw "Version must look like 0.8.0" }
if (git -C $root status --porcelain) { throw "Commit (or stash) your changes first, so the release matches what's on GitHub." }

Write-Host "Building v$Version..."
$log = Join-Path $root 'Logs\release-build.log'
$build = Start-Process -FilePath $unity -Wait -PassThru -ArgumentList @(
    '-batchmode', '-nographics', '-quit', '-projectPath', "`"$root`"",
    '-executeMethod', 'TurnaroundBuilder.BuildWindowsPlayer', '-buildVersion', $Version, '-logFile', "`"$log`"")
if ($build.ExitCode -ne 0) { throw "Unity build failed (exit $($build.ExitCode)); see $log" }

Write-Host "Zipping..."
$builds = Join-Path $root 'Builds'
$stage = Join-Path $builds 'TURNAROUND'
$zip = Join-Path $builds 'TURNAROUND.zip'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Get-ChildItem (Join-Path $builds 'Windows') | Where-Object { $_.Name -notlike '*DoNotShip' } | Copy-Item -Destination $stage -Recurse
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host "Publishing..."
git -C $root add ProjectSettings/ProjectSettings.asset Builds/Windows # the repo always carries the playable build too
git -C $root diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git -C $root commit -m "Release v$Version" | Out-Null }
git -C $root tag "v$Version"
git -C $root push origin HEAD --tags
if ($Notes) { & $gh release create "v$Version" $zip --title "TURNAROUND v$Version" --notes $Notes }
else { & $gh release create "v$Version" $zip --title "TURNAROUND v$Version" --generate-notes }
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Released v$Version. Running copies will offer the update next time they start."

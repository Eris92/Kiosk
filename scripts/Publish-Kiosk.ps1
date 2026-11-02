#Requires -Version 5.1
<#
.SYNOPSIS
Tests, packages and verifies the current commit, then publishes it as an unsigned GitHub pre-release.
.DESCRIPTION
Prints one line per finished step and only grouped failures. Installs nothing on this PC. A signed production
package stays a separate step: see the release section of README.md.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$NoPublish,
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
if (& git status --porcelain --untracked-files=no) { throw 'Commit or stash tracked changes first: a release must match one commit.' }
$commit = (& git rev-parse --short=10 HEAD).Trim()
$branch = (& git branch --show-current).Trim()
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'Test.ps1') -ErrorsOnly -TimeoutSeconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output 'Tests passed.'
}
$packageLog = Join-Path $root 'out\publish-package.log'
New-Item -ItemType Directory -Force -Path (Split-Path $packageLog) | Out-Null
& (Join-Path $PSScriptRoot 'New-Package.ps1') *> $packageLog
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath (Join-Path $root 'out\package') *>> $packageLog
Write-Output "Package verified. Log: $packageLog"
$zip = Join-Path $root "out\Kiosk-win-x64-$commit.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $root 'out\package\*') -DestinationPath $zip
Write-Output "Archive: $zip"
if ($NoPublish) { exit 0 }
if (& git status -sb | Select-String -SimpleMatch 'ahead') { throw 'Push the branch first: the release must point at a commit on GitHub.' }
$tag = "build-$commit"
& gh release create $tag $zip --repo Eris92/Kiosk --prerelease --latest=false --target (& git rev-parse HEAD).Trim() --title "Kiosk $tag ($branch)" --notes 'Unsigned pilot build. Not for production.'
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
Write-Output "Published $tag"
exit 0

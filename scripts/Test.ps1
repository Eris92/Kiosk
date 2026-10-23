#Requires -Version 5.1
[CmdletBinding()]
param([int]$TimeoutSeconds = 120)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Test-Scripts.ps1')
if ($PSVersionTable.PSVersion.Major -ge 7 -and (Get-Command powershell.exe -ErrorAction SilentlyContinue)) {
    & powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'Test-Scripts.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell 5.1 script tests failed.' }
}
& (Join-Path $PSScriptRoot 'Build.ps1') -SelfTest
$directory = Join-Path $root 'out\self-test'
$result = Join-Path $directory 'self-test.txt'
if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
$process = Start-Process -FilePath (Join-Path $directory 'Kiosk.Client.exe') -ArgumentList '--self-test' -WorkingDirectory $directory -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); throw 'Embedded UI self-test timed out.' }
    if (-not (Test-Path -LiteralPath $result)) { throw 'Self-test did not produce its result file.' }
    Get-Content -LiteralPath $result
    if ($process.ExitCode -ne 0) { throw "Embedded UI self-test failed: $($process.ExitCode)." }
} finally { $process.Dispose() }

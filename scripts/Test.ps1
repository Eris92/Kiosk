#Requires -Version 5.1
[CmdletBinding()]
param([int]$TimeoutSeconds = 120)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Test-Scripts.ps1')
if ($PSVersionTable.PSVersion.Major -ge 7 -and (Get-Command powershell.exe -ErrorAction SilentlyContinue)) {
    & powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'Test-Scripts.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell 5.1 script tests failed.' }
}
& (Join-Path $PSScriptRoot 'Build.ps1') -SelfTest
$directory = Join-Path $root 'out\self-test'
$brokerPolicyTest = Join-Path $PSScriptRoot 'Test-BrokerPolicy.ps1'
$brokerPath = Join-Path $directory 'fido\FidoRun.exe'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    & powershell.exe -NoProfile -File $brokerPolicyTest -BrokerPath $brokerPath
    if ($LASTEXITCODE -ne 0) { throw 'Broker policy test failed.' }
} else {
    & $brokerPolicyTest -BrokerPath $brokerPath
}
$result = Join-Path $directory 'self-test.txt'
if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
$process = Start-Process -FilePath (Join-Path $directory 'Kiosk.Client.exe') -ArgumentList '--self-test' -WorkingDirectory $directory -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); throw 'Embedded UI self-test timed out.' }
    if (-not (Test-Path -LiteralPath $result)) { throw 'Self-test did not produce its result file.' }
    Get-Content -LiteralPath $result
    if ($process.ExitCode -ne 0) { throw "Embedded UI self-test failed: $($process.ExitCode)." }
} finally { $process.Dispose() }
$newsDirectory = Join-Path $directory 'news-example'
$newsResult = Join-Path $newsDirectory 'self-test-news.txt'
if (Test-Path -LiteralPath $newsResult) { Remove-Item -LiteralPath $newsResult -Force }
$newsProcess = Start-Process -FilePath (Join-Path $newsDirectory 'KioskKomunikaty.exe') -ArgumentList '--self-test' -WorkingDirectory $newsDirectory -WindowStyle Hidden -PassThru
try {
    if (-not $newsProcess.WaitForExit($TimeoutSeconds * 1000)) { $newsProcess.Kill(); throw 'News editor self-test timed out.' }
    if (-not (Test-Path -LiteralPath $newsResult)) { throw 'News editor self-test did not produce its result file.' }
    Get-Content -LiteralPath $newsResult
    if ($newsProcess.ExitCode -ne 0) { throw "News editor self-test failed: $($newsProcess.ExitCode)." }
} finally { $newsProcess.Dispose() }
$invalidConfig = Join-Path $directory 'invalid-client.json'
try {
    [IO.File]::WriteAllText($invalidConfig, '{"BrowserUrl":"file:///C:/Windows"}')
    $rejected = $false
    try { Assert-KioskClientConfig (Join-Path $directory 'Kiosk.Client.exe') $invalidConfig }
    catch {
        if ($_.Exception.Message -notmatch 'Client configuration validation failed:.*HTTP/HTTPS') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'The deployment preflight accepted an invalid browser URL.' }
} finally {
    if (Test-Path -LiteralPath $invalidConfig) { Remove-Item -LiteralPath $invalidConfig -Force }
}
$service = Start-Process -FilePath (Join-Path $directory 'Kiosk.Client.exe') -ArgumentList '--card-service' -WorkingDirectory $directory -WindowStyle Hidden -PassThru
try {
    if (-not $service.WaitForExit(10000)) { $service.Kill(); throw 'Service-mode entry did not return outside the Windows Service Control Manager.' }
    if ($service.ExitCode -ne 1063) { throw "Unexpected service-mode result outside the Service Control Manager: $($service.ExitCode)." }
} finally { $service.Dispose() }

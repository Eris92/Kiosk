#Requires -Version 5.1
<#
.SYNOPSIS
Installs the LocalSystem Kiosk Card service from a published client.
.DESCRIPTION
The service verifies FIDO2 operations for the local Kiosk only. Run elevated; -WhatIf is read-only.
Payload and service commands are validated before an existing service is stopped. On startup failure,
the previous binary path and startup mode are restored. -Uninstall removes the service, retaining files.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk\CardService'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
Assert-KioskAdministrator
$InstallPath = Assert-KioskProgramPath $InstallPath
$name = 'KioskCard'
$existing = Get-CimInstance Win32_Service -Filter "Name='$name'"
if ($existing -and $existing.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) { throw 'Existing KioskCard service does not run as LocalSystem; review its configuration before updating.' }
if ($Uninstall) {
    if ($existing -and $PSCmdlet.ShouldProcess($name, 'stop and delete Kiosk Card service')) {
        if ($existing.State -ne 'Stopped') { Stop-Service -Name $name -Force }
        Invoke-KioskSc delete $name | Out-Null
    }
    Write-Output 'Kiosk Card service uninstalled; program files were retained.'
    return
}
if (-not $SourcePath) { $SourcePath = Get-KioskClientSource }
$source = (Resolve-Path -LiteralPath $SourcePath).ProviderPath
$sourceExe = Join-Path $source 'Kiosk.Client.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Published client not found: $sourceExe" }
$release = [Guid]::NewGuid().ToString('N')
# Versioned directories prevent a partial file copy from damaging the currently running service.
$target = Join-Path $InstallPath ('release-' + $release)
$binary = '"{0}" --card-service' -f (Join-Path $target 'Kiosk.Client.exe')
if (-not $PSCmdlet.ShouldProcess($name, "copy payload to $target, register and start service")) { return }
Set-KioskDirectoryAcl $InstallPath -UsersRead
New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -ne 'client.json' -and $_.Name -notlike 'self-test*' } |
    Copy-Item -Destination $target -Recurse -Force
if ((Get-FileHash -LiteralPath (Join-Path $target 'Kiosk.Client.exe') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash) { throw 'Service payload copy verification failed.' }
$wasRunning = $existing -and $existing.State -eq 'Running'
try {
    if ($existing) {
        if ($existing.State -ne 'Stopped') { Stop-Service -Name $name -Force }
        Invoke-KioskSc config $name binPath= $binary start= auto | Out-Null
    } else {
        New-Service -Name $name -BinaryPathName $binary -DisplayName 'Kiosk Card' -StartupType Automatic -Description 'Sprawdza PIN karty FIDO2 dla lokalnego Kiosku.' | Out-Null
    }
    Invoke-KioskSc failure $name reset= 86400 actions= restart/5000/restart/15000/none/0 | Out-Null
    Start-Service -Name $name
    (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
} catch {
    $failure = $_
    $failedService = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($failedService -and $failedService.Status -ne 'Stopped') { Stop-Service -Name $name -Force }
    if ($existing) {
        $startMode = switch ($existing.StartMode) { 'Auto' { 'auto' } 'Disabled' { 'disabled' } default { 'demand' } }
        Invoke-KioskSc config $name binPath= $existing.PathName start= $startMode | Out-Null
        if ($wasRunning) { Start-Service -Name $name }
    } else {
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) { Invoke-KioskSc delete $name | Out-Null }
    }
    throw $failure
}
Write-Output "Kiosk Card service running: $binary"

<#
.SYNOPSIS
Installs the "Kiosk Card" service: lets Kiosk check a FIDO2 card PIN and remember the account per card.

.DESCRIPTION
Windows allows only administrators and services to send FIDO2 commands to a card, so Kiosk (a normal user)
asks this service through a local pipe. The service runs as LocalSystem from Program Files (users cannot change
it) and only checks the PIN and returns the card's hmac-secret for "kiosk.local" — no sign-ins for other sites.
Run as administrator after scripts\Build.ps1. Supports -WhatIf; -Uninstall removes the service.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\out\client'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk\CardService'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }

$name = 'KioskCard'
$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($name, 'stop service')) { Stop-Service -Name $name -Force }

if ($Uninstall) {
    if ($existing -and $PSCmdlet.ShouldProcess($name, 'delete service')) { & sc.exe delete $name | Out-Null }
    Write-Output 'Kiosk Card service removed (files are left in place).'
    return
}

$exe = Join-Path $InstallPath 'Kiosk.Client.exe'
if (-not (Test-Path (Join-Path $SourcePath 'Kiosk.Client.exe'))) { throw "Build Kiosk first (scripts\Build.ps1): $SourcePath" }
if ($PSCmdlet.ShouldProcess($InstallPath, 'copy Kiosk')) {
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
}
if ($PSCmdlet.ShouldProcess($name, 'register and start service')) {
    $binary = '"{0}" --card-service' -f $exe
    if ($existing) { & sc.exe config $name binPath= $binary start= auto | Out-Null }
    else { New-Service -Name $name -BinaryPathName $binary -DisplayName 'Kiosk Card' -StartupType Automatic -Description 'Sprawdza PIN karty FIDO2 i odblokowuje zapamiętane konto w Kiosku.' | Out-Null }
    Start-Service -Name $name
}
Write-Output "Done. Service $name runs $exe"

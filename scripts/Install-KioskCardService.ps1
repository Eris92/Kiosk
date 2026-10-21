<#
.SYNOPSIS
Installs the "Kiosk Card" service: lets Kiosk check a FIDO2 card PIN and remember the account per card.

.DESCRIPTION
Windows allows only administrators and services to send FIDO2 commands to a card, so Kiosk (a normal user)
asks this service through a local pipe. The service runs as LocalSystem from Program Files (users cannot change
it) and only checks the PIN and returns the card's hmac-secret for "kiosk.local" — no sign-ins for other sites.
It also installs the latest release from github.com/Eris92/Kiosk when Kiosk asks (Konfiguracja → Aktualizacja),
so updates need no administrator after this script has run once. It is also the watchdog: a Kiosk that crashed or
hangs starts again for its person, and a stopped smart card service (SCardSvr) is started; Windows restarts this service.
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
. (Join-Path $PSScriptRoot 'KioskRuntime.ps1')
Assert-KioskRuntime $SourcePath
if ($PSCmdlet.ShouldProcess($InstallPath, 'copy Kiosk')) {
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
    Remove-KioskOldRuntime $SourcePath $InstallPath
}
if ($PSCmdlet.ShouldProcess($name, 'register and start service')) {
    $binary = '"{0}" --card-service' -f $exe
    if ($existing) { & sc.exe config $name binPath= $binary start= auto | Out-Null }
    else { New-Service -Name $name -BinaryPathName $binary -DisplayName 'Kiosk Card' -StartupType Automatic -Description 'Sprawdza PIN karty FIDO2 i odblokowuje zapamiętane konto w Kiosku.' | Out-Null }
    # Windows restarts the service (and with it the Kiosk watchdog) after a crash: 5 s, 10 s, then every minute.
    & sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
    Start-Service -Name $name
}
Write-Output "Done. Service $name runs $exe"

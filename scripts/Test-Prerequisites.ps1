#Requires -Version 5.1
<#
.SYNOPSIS
Checks a PC before installing the Kiosk: 64-bit Windows, .NET 10 Desktop Runtime, smart card service,
Kiosk Card service, WebView2 and the remote desktops of the configuration. Changes nothing.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = $(if (Test-Path (Join-Path $env:ProgramData 'Kiosk\client.json')) { Join-Path $env:ProgramData 'Kiosk\client.json' } else { Join-Path $PSScriptRoot '..\config\client.json' }),
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\out\client')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KioskRuntime.ps1')
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$cardService = Get-Service 'KioskCard' -ErrorAction SilentlyContinue
$webView = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue
[PSCustomObject]@{
    OS = (Get-CimInstance Win32_OperatingSystem).Caption
    Windows64Bit = [Environment]::Is64BitOperatingSystem
    PowerShell64Bit = [Environment]::Is64BitProcess
    MissingDotNet = $(if (Test-Path $SourcePath) { (@(Get-KioskMissingRuntime $SourcePath) -join ', ') } else { 'brak out\client' })
    WebView2 = $(if ($webView) { $webView.pv } else { 'brak' })
    SmartCardService = [string](Get-Service SCardSvr).Status
    KioskCardService = $(if ($cardService) { [string]$cardService.Status } else { 'nie zainstalowana' })
    RdpActiveX = Test-Path 'Registry::HKEY_CLASSES_ROOT\CLSID\{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}'
    TestMode = [bool]$config.TestMode
} | Format-List
foreach ($rdp in @($config.RdpConnections)) {
    if (-not $rdp) { continue }
    $port = if ($rdp.Port) { $rdp.Port } else { 3389 }
    $reachable = Test-NetConnection -ComputerName $rdp.Server -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
    Write-Output ("Pulpit zdalny {0} ({1}:{2}): {3}" -f $rdp.Name, $rdp.Server, $port, $(if ($reachable) { 'osiągalny' } else { 'NIEOSIĄGALNY' }))
}
Write-Output 'Diagnostyka czytnika i karty: certutil.exe -scinfo'

<#
.SYNOPSIS
Simple installation for preparing the configuration: the Kiosk in Program Files and its configuration, nothing else.

.DESCRIPTION
For an administrator's PC where the configuration (client.json, Menu, Kategorie, news, logo) is prepared and tested.
  * Copies the Kiosk build to -InstallPath and, when there is none yet, the configuration to -ConfigPath
    (administrators: full control, users: read only). An existing configuration is kept as it is.
  * Adds two shortcuts (Start menu and desktop):
      "Kiosk - Konfiguracja" opens only Konfiguracja (as administrator);
      "Kiosk" starts the Kiosk in a window of this account, to see the result.
  * No Kiosk Card service, no shell change, no sign-in screen or registry settings, Explorer stays.
    Card sign-in and updates without an administrator need the full installation (Install-KioskShell.ps1 and
    Install-KioskCardService.ps1).
-Uninstall removes the shortcuts and the program folder; the configuration stays.
Run as administrator. Supports -WhatIf.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\scripts\Install-KioskConfig.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\out\client'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk'),
    [string]$ConfigPath = (Join-Path $env:ProgramData 'Kiosk\client.json'),
    [string]$SourceConfig = (Join-Path $PSScriptRoot '..\config\client.json'),
    [switch]$NoDesktopShortcuts,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }

$exe = Join-Path $InstallPath 'Kiosk.Client.exe'
$folders = @([Environment]::GetFolderPath('CommonPrograms'))
if (-not $NoDesktopShortcuts) { $folders += [Environment]::GetFolderPath('CommonDesktopDirectory') }
$shortcuts = foreach ($folder in $folders) { (Join-Path $folder 'Kiosk - Konfiguracja.lnk'), (Join-Path $folder 'Kiosk.lnk') }

if ($Uninstall) {
    foreach ($link in $shortcuts) { if ((Test-Path $link) -and $PSCmdlet.ShouldProcess($link, 'remove shortcut')) { Remove-Item $link -Force } }
    if ((Test-Path $InstallPath) -and $PSCmdlet.ShouldProcess($InstallPath, 'remove Kiosk')) {
        Get-Process Kiosk.Client -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallPath\*" } | Stop-Process -Force
        Remove-Item $InstallPath -Recurse -Force
    }
    Write-Output "Kiosk removed. The configuration stays in $ConfigPath."
    return
}

# 1. Program for everyone.
if (-not (Test-Path (Join-Path $SourcePath 'Kiosk.Client.exe'))) { throw "No Kiosk build in $SourcePath (unzip Kiosk-win-x64.zip or run scripts\Build.ps1)." }
. (Join-Path $PSScriptRoot 'KioskRuntime.ps1')
Assert-KioskRuntime $SourcePath
if ($PSCmdlet.ShouldProcess($InstallPath, 'copy Kiosk')) {
    Get-Process Kiosk.Client -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallPath\*" } | Stop-Process -Force
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
    Remove-KioskOldRuntime $SourcePath $InstallPath
}

# 2. Configuration: copied only when there is none; writable by administrators only (Konfiguracja saves as one).
if ($PSCmdlet.ShouldProcess($ConfigPath, 'write configuration when missing')) {
    $directory = Split-Path $ConfigPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if (Test-Path $ConfigPath) { Write-Output "Configuration kept: $ConfigPath" }
    else {
        Copy-Item $SourceConfig $ConfigPath
        # The release package carries the example messages once, in out\client\news-example.
        $news = @((Join-Path (Split-Path $SourceConfig) 'news'), (Join-Path $SourcePath 'news-example')) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($news) { Copy-Item $news (Join-Path $directory 'news') -Recurse -Force }
        Write-Output "Configuration copied: $ConfigPath"
    }
    & icacls.exe $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $directory" }
}

# 3. Shortcuts.
function New-Shortcut([string]$Path, [string]$Arguments, [string]$Description, [switch]$AsAdministrator) {
    if (-not $PSCmdlet.ShouldProcess($Path, 'create shortcut')) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $exe
    $link.Arguments = $Arguments
    $link.WorkingDirectory = $InstallPath
    $link.IconLocation = "$exe,0"
    $link.Description = $Description
    $link.Save()
    if ($AsAdministrator) {
        # "Run as administrator" flag of a .lnk file (byte 0x15, bit 0x20).
        $bytes = [IO.File]::ReadAllBytes($Path)
        $bytes[0x15] = $bytes[0x15] -bor 0x20
        [IO.File]::WriteAllBytes($Path, $bytes)
    }
}
foreach ($folder in $folders) {
    New-Shortcut (Join-Path $folder 'Kiosk - Konfiguracja.lnk') ('--settings "{0}"' -f $ConfigPath) 'Kiosk: Konfiguracja' -AsAdministrator
    New-Shortcut (Join-Path $folder 'Kiosk.lnk') ('"{0}"' -f $ConfigPath) 'Kiosk'
}

Write-Output "Done. Kiosk: $exe"
Write-Output "Open 'Kiosk - Konfiguracja' from the Start menu or the desktop to edit $ConfigPath."

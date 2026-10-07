<#
.SYNOPSIS
Makes this PC a shared card kiosk: every person signs in to Windows with their card or Windows Hello
and gets the Kiosk instead of Explorer (full SSO for Office, OneDrive, RDP and the browser).

.DESCRIPTION
Run as administrator on a domain-joined or Intune (Entra ID) PC. For Intune-managed fleets prefer the
Shell Launcher profile in config/intune (this script does the same locally, e.g. for testing).

  * Copies the Kiosk build to -InstallPath and the configuration to -ConfigPath
    (administrators: full control, users: read only). SessionMode is set to WindowsShell.
  * Shell:
      ShellLauncher (Windows Enterprise / Education / IoT Enterprise): Kiosk is the shell for everyone
        except local Administrators, who keep Explorer.
      DefaultProfile (Windows Pro): the Kiosk shell is written into the default user profile, so every
        account that signs in for the first time gets it; existing profiles (your admin account) keep Explorer.
  * Sign-in screen: no last user name, fast user switching on (disconnected sessions keep running),
    Windows' own card-removal action off (the Kiosk disconnects the session itself).
  * -RequireSmartCard additionally forbids password sign-in for everyone. Test card sign-in first.

-Uninstall reverts the shell and sign-in settings (files are left in place).
Supports -WhatIf. Nothing here bypasses execution policy; sign the script per your policy.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\out\client'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk'),
    [string]$ConfigPath = (Join-Path $env:ProgramData 'Kiosk\client.json'),
    [string]$SourceConfig = (Join-Path $PSScriptRoot '..\config\client.json'),
    [ValidateSet('Auto', 'ShellLauncher', 'DefaultProfile')][string]$Method = 'Auto',
    [switch]$RequireSmartCard,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }

$exe = Join-Path $InstallPath 'Kiosk.Client.exe'
$shellCommand = '"{0}" "{1}"' -f $exe, $ConfigPath
$system = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$edition = (Get-CimInstance Win32_OperatingSystem).Caption
if ($Method -eq 'Auto') { $Method = if ($edition -match 'Enterprise|Education|IoT') { 'ShellLauncher' } else { 'DefaultProfile' } }
Write-Output "Windows: $edition; method: $Method"

function Set-Value($Path, $Name, $Value, $Type = 'DWord') {
    if ($PSCmdlet.ShouldProcess("$Path\$Name", "set $Value")) {
        if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    }
}

function Invoke-DefaultUserHive([scriptblock]$Action) {
    # The default profile is copied for every account that signs in for the first time.
    $hive = Join-Path $env:SystemDrive 'Users\Default\NTUSER.DAT'
    if (-not $PSCmdlet.ShouldProcess($hive, 'edit default user profile')) { return }
    & reg.exe load 'HKU\KioskDefault' $hive | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot load $hive" }
    try { & $Action 'Registry::HKEY_USERS\KioskDefault\Software\Microsoft\Windows\CurrentVersion\Policies\System' }
    finally { [GC]::Collect(); [GC]::WaitForPendingFinalizers(); & reg.exe unload 'HKU\KioskDefault' | Out-Null }
}

function Get-ShellLauncher {
    Get-CimInstance -Namespace 'root\standardcimv2\embedded' -ClassName WESL_UserSetting -ErrorAction Stop
}

if ($Uninstall) {
    if ($Method -eq 'ShellLauncher') {
        if ($PSCmdlet.ShouldProcess('Shell Launcher', 'disable')) {
            $launcher = Get-ShellLauncher
            Invoke-CimMethod -InputObject $launcher -MethodName SetEnabled -Arguments @{ Enabled = $false } | Out-Null
        }
    } else {
        Invoke-DefaultUserHive { param($key) if (Test-Path $key) { Remove-ItemProperty -Path $key -Name Shell -ErrorAction SilentlyContinue } }
    }
    Set-Value $system 'dontdisplaylastusername' 0
    Set-Value $system 'scforceoption' 0
    Write-Output 'Kiosk shell removed. Profiles created meanwhile keep their own Shell value (HKCU\...\Policies\System\Shell).'
    return
}

# 1. Files: program for everyone, configuration writable by administrators only.
if (-not (Test-Path (Join-Path $SourcePath 'Kiosk.Client.exe'))) { throw "Build the client first (scripts\Build.ps1): $SourcePath" }
if ($PSCmdlet.ShouldProcess($InstallPath, 'copy Kiosk')) {
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
}
if ($PSCmdlet.ShouldProcess($ConfigPath, 'write configuration (SessionMode = WindowsShell)')) {
    $directory = Split-Path $ConfigPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if (-not (Test-Path $ConfigPath)) { Copy-Item $SourceConfig $ConfigPath }
    $json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName SessionMode -NotePropertyValue 'WindowsShell' -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding utf8
    # SYSTEM and Administrators: full control; Users: read. Settings saved from the Kiosk need an admin.
    & icacls.exe $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $directory" }
}

# 2. Shell.
if ($Method -eq 'ShellLauncher') {
    if ($PSCmdlet.ShouldProcess('Client-EmbeddedShellLauncher', 'enable feature and set Kiosk as default shell')) {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher
        if ($feature.State -ne 'Enabled') { Enable-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher -NoRestart | Out-Null }
        $launcher = Get-ShellLauncher
        # DefaultAction 0 = restart the shell if it is closed.
        Invoke-CimMethod -InputObject $launcher -MethodName SetDefaultShell -Arguments @{ Shell = $shellCommand; DefaultAction = 0 } | Out-Null
        Invoke-CimMethod -InputObject $launcher -MethodName SetCustomShell -Arguments @{ Sid = 'S-1-5-32-544'; Shell = 'explorer.exe'; DefaultAction = 0 } | Out-Null
        Invoke-CimMethod -InputObject $launcher -MethodName SetEnabled -Arguments @{ Enabled = $true } | Out-Null
    }
} else {
    Invoke-DefaultUserHive { param($key) if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }; New-ItemProperty -Path $key -Name Shell -Value $shellCommand -PropertyType String -Force | Out-Null }
}

# 3. Sign-in screen and sessions.
Set-Value $system 'dontdisplaylastusername' 1          # No list of previous users.
Set-Value $system 'HideFastUserSwitching' 0            # Disconnected sessions stay; the next person signs in alongside.
Set-Value $winlogon 'ScRemoveOption' '0' 'String'      # Windows does nothing on card removal; the Kiosk disconnects.
if ($RequireSmartCard) { Set-Value $system 'scforceoption' 1 }   # Card (or Windows Hello for Business) only.
Set-Service -Name SCardSvr -StartupType Automatic -WhatIf:$WhatIfPreference

Write-Output "Done. Kiosk shell: $shellCommand"
Write-Output 'Sign out and sign in with a card as a non-administrator account to test.'

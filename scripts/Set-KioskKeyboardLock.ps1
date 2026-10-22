<#
.SYNOPSIS
Locks Ctrl+Alt+Del and the other Windows shortcuts on a kiosk PC in Windows itself, so they stay locked also
while the Kiosk is closed (Alt+F4 the Kiosk blocks itself).

.DESCRIPTION
No program can catch Ctrl+Alt+Del: Windows handles it before any application sees the keys. So this is a setting
of the PC, not of the Kiosk. Run as administrator; the script picks what this edition of Windows offers:

  * Keyboard Filter (Windows Enterprise / Education / IoT Enterprise): Ctrl+Alt+Del, Ctrl+Shift+Esc and Win+L
    do nothing for everyone except administrators (they keep every shortcut). The feature is turned on if needed;
    Windows may then need a restart before the filter works.
  * Windows Pro / Business (no Keyboard Filter): Ctrl+Alt+Del still opens Windows' screen, but it offers nothing
    any more: no Task Manager, no Lock, no Sign out, no Change password. Only "Przełącz użytkownika" (fast user
    switching, also in Konfiguracja) and Cancel remain. These policies are for the whole PC, administrators included.

-Uninstall turns the filter or the policies off again. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Uninstall,
    # Pro: also hide "Przełącz użytkownika" on the Ctrl+Alt+Del screen (Konfiguracja's button keeps working).
    [switch]$HideSwitchUser
)

$ErrorActionPreference = 'Stop'
# 32-bit PowerShell (some deployment tools) sees Program Files (x86) and the 32-bit registry: the Kiosk would land in the wrong place.
if (-not [Environment]::Is64BitProcess) { throw 'Uruchom 64-bitowy PowerShell (C:\Windows\Sysnative\WindowsPowerShell\v1.0\powershell.exe).' }
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Uruchom jako administrator.' }

$keys = 'Ctrl+Alt+Del', 'Ctrl+Shift+Esc', 'Win+L'
$system = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$explorer = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer'
# What the Ctrl+Alt+Del screen offers on Windows Pro: each one off.
$policies = @(
    @{ Key = $system; Name = 'DisableTaskMgr' },          # Menedżer zadań
    @{ Key = $system; Name = 'DisableLockWorkstation' },  # Zablokuj (the lock is the Kiosk's own)
    @{ Key = $system; Name = 'DisableChangePassword' },   # Zmień hasło
    @{ Key = $explorer; Name = 'NoLogoff' }               # Wyloguj
)

function Get-KeyboardFilter {
    try { Get-WindowsOptionalFeature -Online -FeatureName Client-KeyboardFilter -ErrorAction Stop } catch { $null }
}

$feature = Get-KeyboardFilter
if ($feature) {
    if (-not $Uninstall -and $feature.State -ne 'Enabled' -and $PSCmdlet.ShouldProcess('Client-KeyboardFilter', 'enable feature')) {
        $result = Enable-WindowsOptionalFeature -Online -FeatureName Client-KeyboardFilter -All -NoRestart
        if ($result.RestartNeeded) { Write-Warning 'Filtr klawiatury zadziała po ponownym uruchomieniu Windows. Uruchom potem ten skrypt jeszcze raz.'; return }
    }
    $namespace = 'root\standardcimv2\embedded'
    if ($PSCmdlet.ShouldProcess('Keyboard Filter', ($(if ($Uninstall) { 'unblock ' } else { 'block ' }) + ($keys -join ', ')))) {
        # Administrators keep every shortcut (and can always undo this).
        $setting = Get-CimInstance -Namespace $namespace -ClassName WEKF_Settings | Where-Object Name -eq 'DisableKeyboardFilterForAdministrators'
        if ($setting) { Set-CimInstance -InputObject $setting -Property @{ Value = 'true' } }
        foreach ($id in $keys) {
            $key = Get-CimInstance -Namespace $namespace -ClassName WEKF_PredefinedKey | Where-Object Id -eq $id
            if (-not $key) { Write-Warning "Filtr klawiatury nie zna skrótu $id."; continue }
            Set-CimInstance -InputObject $key -Property @{ Enabled = -not $Uninstall }
        }
        Set-Service -Name MsKeyboardFilter -StartupType Automatic
        if (-not $Uninstall) { Start-Service -Name MsKeyboardFilter }
    }
    Write-Host ($(if ($Uninstall) { 'Filtr klawiatury: skróty odblokowane.' } else { 'Filtr klawiatury: zablokowane ' + ($keys -join ', ') + ' (poza administratorami).' }))
    return
}

# Windows Pro / Business: the Ctrl+Alt+Del screen with nothing on it.
$all = $policies + $(if ($HideSwitchUser) { @(@{ Key = $system; Name = 'HideFastUserSwitching' }) } else { @() })
foreach ($p in $all) {
    if (-not $PSCmdlet.ShouldProcess("$($p.Key)\$($p.Name)", $(if ($Uninstall) { 'remove' } else { 'set 1' }))) { continue }
    if ($Uninstall) {
        if ($p.Name -eq 'HideFastUserSwitching') { New-ItemProperty -Path $p.Key -Name $p.Name -Value 0 -PropertyType DWord -Force | Out-Null }   # Install-KioskShell.ps1 keeps it on.
        else { Remove-ItemProperty -Path $p.Key -Name $p.Name -ErrorAction SilentlyContinue }
    } else {
        if (-not (Test-Path $p.Key)) { New-Item -Path $p.Key -Force | Out-Null }
        New-ItemProperty -Path $p.Key -Name $p.Name -Value 1 -PropertyType DWord -Force | Out-Null
    }
}
Write-Host ($(if ($Uninstall) { 'Ekran Ctrl+Alt+Del: opcje przywrócone.' }
    else { 'Ta edycja Windows nie ma Filtra klawiatury: Ctrl+Alt+Del otworzy ekran Windows, ale bez Menedżera zadań, blokady, wylogowania i zmiany hasła. Działa po ponownym zalogowaniu.' }))

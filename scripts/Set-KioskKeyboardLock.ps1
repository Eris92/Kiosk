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

-Uninstall restores recorded filter keys and administrator setting or recorded Pro policies. The Windows
Keyboard Filter feature remains installed after uninstall. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Uninstall,
    # Pro: also hide "Przełącz użytkownika" on the Ctrl+Alt+Del screen (Konfiguracja's button keeps working).
    [switch]$HideSwitchUser
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
# 32-bit PowerShell (some deployment tools) sees Program Files (x86) and the 32-bit registry: the Kiosk would land in the wrong place.
if (-not [Environment]::Is64BitProcess) { throw 'Uruchom 64-bitowy PowerShell (C:\Windows\Sysnative\WindowsPowerShell\v1.0\powershell.exe).' }
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Uruchom jako administrator.' }

$keys = 'Ctrl+Alt+Del', 'Shift+Ctrl+Esc', 'Win+L'
$system = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$explorer = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer'
# What the Ctrl+Alt+Del screen offers on Windows Pro: each one off.
$policies = @(
    @{ Key = $system; Name = 'DisableTaskMgr' },          # Menedżer zadań
    @{ Key = $system; Name = 'DisableLockWorkstation' },  # Zablokuj (the lock is the Kiosk's own)
    @{ Key = $system; Name = 'DisableChangePassword' },   # Zmień hasło
    @{ Key = $explorer; Name = 'NoLogoff' }               # Wyloguj
)

$edition = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID).EditionID
# The feature can be listed as Disabled on Pro even though that edition cannot use Keyboard Filter.
$feature = if ($edition -match 'Enterprise|Education|IoT') {
    Get-WindowsOptionalFeature -Online -FeatureName Client-KeyboardFilter -ErrorAction Stop
} else { $null }
if ($feature) {
    $stateFile = Join-Path $env:ProgramData 'Kiosk\DeploymentState\keyboard-filter.clixml'
    $namespace = 'root\standardcimv2\embedded'
    if (-not $Uninstall -and $feature.State -ne 'Enabled') {
        if ($feature.State -eq 'EnablePending') {
            Write-Warning 'Uruchom ponownie Windows, a następnie ponów ten skrypt, aby skonfigurować reguły filtra.'
            return
        }
        if ($PSCmdlet.ShouldProcess('Client-KeyboardFilter', 'enable feature')) {
            Enable-WindowsOptionalFeature -Online -FeatureName Client-KeyboardFilter -All -NoRestart | Out-Null
            Write-Warning 'Uruchom ponownie Windows, a następnie ponów ten skrypt, aby skonfigurować reguły filtra.'
        }
        return
    }
    if ($feature.State -ne 'Enabled') {
        throw 'Keyboard Filter is disabled; enable it and restart Windows before restoring saved rule values.'
    }
    if ($Uninstall -and -not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        throw 'No saved Keyboard Filter state exists; do not change rules configured by another administrator.'
    }
    # Resolve all documented WMI IDs before changing any rule; a missing rule is an error.
    $available = @(Get-CimInstance -Namespace $namespace -ClassName WEKF_PredefinedKey -ErrorAction Stop)
    $rules = @(Resolve-KioskKeyboardFilterKeys $available)
    $settings = @(Get-CimInstance -Namespace $namespace -ClassName WEKF_Settings -ErrorAction Stop |
        Where-Object Name -eq 'DisableKeyboardFilterForAdministrators')
    if ($settings.Count -ne 1) { throw 'Keyboard Filter administrator bypass setting is missing or duplicated.' }
    $setting = $settings[0]
    $service = Get-Service -Name MsKeyboardFilter -ErrorAction Stop
    if ($service.Status -ne 'Running') { throw 'Keyboard Filter service is not running; restart Windows before configuring rules.' }

    if ($Uninstall) {
        if ($PSCmdlet.ShouldProcess('Keyboard Filter', 'restore saved rules and administrator setting where unchanged')) {
            $journal = Open-KioskRegistryJournal 'keyboard-filter'
            $saved = $journal.State.Filter
            if (-not $saved -or $saved.Version -ne 1) { throw 'Saved Keyboard Filter state is missing or unsupported.' }
            $seen = @{}
            foreach ($entry in @($saved.Rules)) {
                if ($entry.Id -notin $keys -or $seen.ContainsKey([string]$entry.Id) -or
                    [bool]$entry.Applied -ne $true) {
                    throw 'Saved Keyboard Filter rules are invalid or duplicated.'
                }
                $seen[[string]$entry.Id] = $true
            }
            if ($null -ne $saved.Administrators -and [string]$saved.Administrators.Applied -ine 'true') {
                throw 'Saved Keyboard Filter administrator setting is invalid.'
            }
            $remaining = @()
            foreach ($entry in @($saved.Rules)) {
                $rule = @($rules | Where-Object Id -eq $entry.Id)
                if ($rule.Count -ne 1) { throw "Saved Keyboard Filter rule is invalid: $($entry.Id)" }
                $current = [bool]$rule[0].Enabled
                if ($current -eq [bool]$entry.Original) { continue }
                if ($current -ne [bool]$entry.Applied) {
                    Write-Warning "Keyboard Filter rule changed after Kiosk deployment; leaving it in place: $($entry.Id)"
                    $remaining += $entry
                    continue
                }
                Set-CimInstance -InputObject $rule[0] -Property @{ Enabled = [bool]$entry.Original } -ErrorAction Stop | Out-Null
            }
            $saved.Rules = $remaining
            if ($null -ne $saved.Administrators) {
                $current = [string]$setting.Value
                if ($current -ieq [string]$saved.Administrators.Original) { $saved.Administrators = $null }
                elseif ($current -ieq [string]$saved.Administrators.Applied) {
                    Set-CimInstance -InputObject $setting -Property @{ Value = [string]$saved.Administrators.Original } -ErrorAction Stop | Out-Null
                    $saved.Administrators = $null
                } else { Write-Warning 'Keyboard Filter administrator setting changed after Kiosk deployment; leaving it in place.' }
            }
            if ($saved.Rules.Count -eq 0 -and $null -eq $saved.Administrators) { $journal.State.Filter = $null }
            Save-KioskRegistryJournal $journal
            if ($null -eq $journal.State.Filter) { Remove-Item -LiteralPath $journal.File -Force }
        }
        if ($WhatIfPreference) { Write-Output 'WhatIf: zapisane reguły Filtra klawiatury zostałyby przywrócone.' }
        else { Write-Output 'Filtr klawiatury: zapisane reguły przywrócono tam, gdzie pozostały bez zmian. Funkcja Windows pozostaje włączona.' }
        return
    }

    if ($PSCmdlet.ShouldProcess('Keyboard Filter', 'record previous values and block required keys')) {
        $journal = Open-KioskRegistryJournal 'keyboard-filter'
        $saved = $journal.State.Filter
        if ($saved) {
            if ($saved.Version -ne 1 -or @($saved.Rules).Count -ne $rules.Count -or
                $null -eq $saved.Administrators -or [string]$saved.Administrators.Applied -ine 'true') {
                throw 'Saved Keyboard Filter state is incomplete; restore it before reinstalling.'
            }
            $seen = @{}
            foreach ($entry in @($saved.Rules)) {
                $rule = @($rules | Where-Object Id -eq $entry.Id)
                if ($seen.ContainsKey([string]$entry.Id) -or $rule.Count -ne 1 -or
                    [bool]$entry.Applied -ne $true -or [bool]$rule[0].Enabled -ne [bool]$entry.Applied) {
                    throw "Keyboard Filter rule changed since Kiosk deployment: $($entry.Id)"
                }
                $seen[[string]$entry.Id] = $true
            }
            if ([string]$setting.Value -ine [string]$saved.Administrators.Applied) {
                throw 'Keyboard Filter administrator setting changed since Kiosk deployment.'
            }
        } else {
            $saved = @{
                Version = 1
                Rules = @($rules | ForEach-Object { @{ Id = $_.Id; Original = [bool]$_.Enabled; Applied = $true } })
                Administrators = @{ Original = [string]$setting.Value; Applied = 'true' }
            }
            $journal.State.Filter = $saved
            Save-KioskRegistryJournal $journal
        }
        if ([string]$setting.Value -ine 'true') {
            Set-CimInstance -InputObject $setting -Property @{ Value = 'true' } -ErrorAction Stop | Out-Null
        }
        foreach ($rule in $rules) {
            if (-not [bool]$rule.Enabled) { Set-CimInstance -InputObject $rule -Property @{ Enabled = $true } -ErrorAction Stop | Out-Null }
        }
    }
    if ($WhatIfPreference) { Write-Output 'WhatIf: ustawienia Filtra klawiatury nie zostały zmienione.' }
    else { Write-Output ('Filtr klawiatury: zablokowane ' + ($keys -join ', ') + ' (poza administratorami).') }
    return
}

# Windows Pro / Business: the Ctrl+Alt+Del screen with nothing on it.
$all = $policies + $(if ($HideSwitchUser) { @(@{ Key = $system; Name = 'HideFastUserSwitching' }) } else { @() })
if ($Uninstall) {
    $state = Join-Path $env:ProgramData 'Kiosk\DeploymentState\keyboard-lock.clixml'
    if (-not (Test-Path -LiteralPath $state)) { throw 'No saved keyboard policy state exists; do not remove policies set by another administrator.' }
    if ($PSCmdlet.ShouldProcess('Windows Pro keyboard policies', 'restore recorded values where unchanged')) {
        $journal = Open-KioskRegistryJournal 'keyboard-lock'
        Restore-KioskRegistryJournal $journal
    }
} else {
    $journal = $null
    foreach ($p in $all) {
        if (-not $PSCmdlet.ShouldProcess("$($p.Key)\$($p.Name)", 'set 1 and record previous value')) { continue }
        if (-not $journal) { $journal = Open-KioskRegistryJournal 'keyboard-lock' }
        Set-KioskRegistryValue $journal $p.Key $p.Name 1 'DWord'
    }
}
if ($WhatIfPreference) { Write-Output 'WhatIf: zasady Ctrl+Alt+Del nie zostaly zmienione.' }
else { Write-Host ($(if ($Uninstall) { 'Ekran Ctrl+Alt+Del: zapisane wartosci przywrocone tam, gdzie nie zostaly zmienione pozniej.' }
    else { 'Ta edycja Windows nie ma Filtra klawiatury: Ctrl+Alt+Del otworzy ekran Windows, ale bez Menedżera zadań, blokady, wylogowania i zmiany hasła. Działa po ponownym zalogowaniu.' })) }

#Requires -Version 5.1
<#
.SYNOPSIS
Installs Kiosk as the Windows shell without changing existing interactive sessions.
.DESCRIPTION
Run elevated on a test device first. ShellLauncher requires Enterprise/Education/IoT Enterprise;
DefaultProfile applies only to profiles created after installation. -WhatIf changes nothing.
Registry values and the previous Shell Launcher configuration are retained in protected deployment state.
-Uninstall restores recorded values; existing user profiles and installed payloads are retained.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk'),
    [string]$ConfigPath = (Join-Path $env:ProgramData 'Kiosk\client.json'),
    [string]$SourceConfig = '',
    [ValidateSet('Auto', 'ShellLauncher', 'DefaultProfile')][string]$Method = 'Auto',
    [switch]$RequireSmartCard,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
Assert-KioskAdministrator
if (-not $SourceConfig) { $SourceConfig = Join-Path $PSScriptRoot '..\config\client.json' }
$InstallPath = Assert-KioskProgramPath $InstallPath
$ConfigPath = Assert-KioskDataFilePath $ConfigPath '.json'
$journal = $null
$system = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$namespace = 'root\standardcimv2\embedded'
function Journal {
    if (-not $script:journal) { $script:journal = Open-KioskRegistryJournal 'shell' }
    return $script:journal
}
function Set-Value($Path, $Name, $Value, $Type = 'DWord') {
    if ($PSCmdlet.ShouldProcess("$Path\$Name", 'set shell policy (previous value retained)')) {
        Set-KioskRegistryValue (Journal) $Path $Name $Value $Type
    }
}
function Invoke-Launcher([string]$MethodName, [hashtable]$Arguments = @{}) {
    $result = Invoke-CimMethod -Namespace $namespace -ClassName WESL_UserSetting -MethodName $MethodName -Arguments $Arguments
    if ($result.ReturnValue -ne 0) { throw "Shell Launcher $MethodName failed: $($result.ReturnValue)" }
    return $result
}
function Get-LauncherAdmin {
    $admin = Get-CimInstance -Namespace $namespace -ClassName WESL_UserSetting |
        Where-Object { $_.Sid -eq 'S-1-5-32-544' } | Select-Object -First 1
    if (-not $admin) { return $null }
    return @{
        Shell = [string]$admin.Shell
        DefaultAction = [int]$admin.DefaultAction
        CustomReturnCodes = @($admin.CustomReturnCodes)
        CustomReturnCodesAction = @($admin.CustomReturnCodesAction)
    }
}
function Test-LauncherAdminEqual($Expected, $Current) {
    if ($null -eq $Expected -or $null -eq $Current) { return $null -eq $Expected -and $null -eq $Current }
    if ($Expected.Shell -ne $Current.Shell -or [int]$Expected.DefaultAction -ne [int]$Current.DefaultAction) { return $false }
    foreach ($name in @('CustomReturnCodes', 'CustomReturnCodesAction')) {
        $left = @($Expected.$name | Where-Object { $null -ne $_ })
        $right = @($Current.$name | Where-Object { $null -ne $_ })
        if (($left -join ',') -ne ($right -join ',')) { return $false }
    }
    return $true
}
function Invoke-DefaultUserHive([scriptblock]$Action) {
    $hive = Join-Path $env:SystemDrive 'Users\Default\NTUSER.DAT'
    if (-not $PSCmdlet.ShouldProcess($hive, 'edit the default profile shell')) { return }
    if (Test-Path 'Registry::HKEY_USERS\KioskDefault') { throw 'HKU\KioskDefault is already mounted; do not overwrite another operation.' }
    & reg.exe load 'HKU\KioskDefault' $hive | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot load $hive" }
    try { & $Action 'Registry::HKEY_USERS\KioskDefault\Software\Microsoft\Windows\CurrentVersion\Policies\System' }
    finally {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload 'HKU\KioskDefault' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not unload the default profile hive; close registry handles and unload HKU\KioskDefault before signing in.' }
    }
}
$stateFile = Join-Path $env:ProgramData 'Kiosk\DeploymentState\shell.clixml'
if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $stateFile)) { throw 'No saved deployment state exists; restore the previous shell/policies from your management system or export.' }
    if (-not $PSCmdlet.ShouldProcess('Kiosk shell', 'restore recorded shell and policy settings')) { return }
    $journal = Journal
    $Method = $journal.State.Method
    if ($Method -eq 'ShellLauncher' -and $journal.State.Launcher) {
        $original = $journal.State.Launcher
        $current = Invoke-Launcher 'GetDefaultShell'
        if ($current.Shell -ne $journal.State.AppliedShell) { throw 'The default shell changed after Kiosk installation; refusing to overwrite a later deployment.' }
        $appliedAdmin = $journal.State.AppliedAdmin
        if (-not $appliedAdmin) { $appliedAdmin = @{ Shell = 'explorer.exe'; DefaultAction = 0; CustomReturnCodes = @(); CustomReturnCodesAction = @() } }
        if (-not (Test-LauncherAdminEqual $appliedAdmin (Get-LauncherAdmin))) { throw 'The Administrators shell changed after Kiosk installation; refusing to overwrite a later deployment.' }
        $enabled = Invoke-Launcher 'IsEnabled'
        $appliedEnabled = if ($null -eq $journal.State.AppliedEnabled) { $true } else { [bool]$journal.State.AppliedEnabled }
        if ([bool]$enabled.Enabled -ne $appliedEnabled) { throw 'Shell Launcher enablement changed after Kiosk installation; refusing to overwrite a later deployment.' }
        Invoke-Launcher 'SetDefaultShell' @{ Shell = $original.Shell; DefaultAction = [int]$original.DefaultAction } | Out-Null
        if ($original.Admin) {
            Invoke-Launcher 'SetCustomShell' @{ Sid = 'S-1-5-32-544'; Shell = $original.Admin.Shell; DefaultAction = [int]$original.Admin.DefaultAction; CustomReturnCodes = [int[]]$original.Admin.CustomReturnCodes; CustomReturnCodesAction = [int[]]$original.Admin.CustomReturnCodesAction } | Out-Null
        } else { Invoke-Launcher 'RemoveCustomShell' @{ Sid = 'S-1-5-32-544' } | Out-Null }
        Invoke-Launcher 'SetEnabled' @{ Enabled = [bool]$original.Enabled } | Out-Null
        $journal.State.Launcher = $null
        $journal.State.AppliedAdmin = $null
        $journal.State.AppliedEnabled = $null
        Save-KioskRegistryJournal $journal
    }
    if ($Method -eq 'DefaultProfile') { Invoke-DefaultUserHive { param($key) Restore-KioskRegistryJournal $script:journal } }
    else { Restore-KioskRegistryJournal $journal }
    Write-Output 'Recorded shell/policy state restored. Installed files, configuration and already-created user profiles were retained.'
    return
}
if (-not $SourcePath) { $SourcePath = Get-KioskClientSource }
$source = (Resolve-Path -LiteralPath $SourcePath).ProviderPath
$sourceExe = Join-Path $source 'Kiosk.Client.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw 'Publish the production client first.' }
$edition = (Get-CimInstance Win32_OperatingSystem).Caption
if ($Method -eq 'Auto') { $Method = if ($edition -match 'Enterprise|Education|IoT') { 'ShellLauncher' } else { 'DefaultProfile' } }
$configSource = Get-KioskInstallConfigSource $ConfigPath $SourceConfig ($PSBoundParameters.ContainsKey('SourceConfig'))
$json = Get-Content -LiteralPath $configSource -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $json.SessionMode) { $json | Add-Member -NotePropertyName SessionMode -NotePropertyValue 'SelfService' -Force }
$json | Add-Member -NotePropertyName TestMode -NotePropertyValue $false -Force
$candidateJson = $json | ConvertTo-Json -Depth 16
$release = Join-Path $InstallPath ('ClientVersions\release-' + [Guid]::NewGuid().ToString('N'))
$exe = Join-Path $release 'Kiosk.Client.exe'
$shellCommand = '"{0}" "{1}"' -f $exe, [IO.Path]::GetFullPath($ConfigPath)
Write-Output "Windows: $edition; method: $Method"
Write-Output "Configuration source: $configSource"
if (-not $PSCmdlet.ShouldProcess($InstallPath, 'copy an isolated client version and configure the shell for future sign-ins')) { return }
$candidate = Join-Path ([IO.Path]::GetTempPath()) ('kiosk-shell-config-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($candidate, $candidateJson, [Text.UTF8Encoding]::new($false))
    Assert-KioskClientConfig $sourceExe $candidate
} finally {
    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
}
$journal = Journal
if ($journal.State.Method -and $journal.State.Method -ne $Method) { throw 'Uninstall the recorded shell method before changing installation methods.' }
Set-KioskDirectoryAcl $InstallPath -UsersRead
New-Item -ItemType Directory -Force -Path $release | Out-Null
Copy-KioskPayloadVerified $source $release
$directory = Split-Path -Parent $ConfigPath
Set-KioskDirectoryAcl $directory -UsersRead
if (Test-Path -LiteralPath $ConfigPath) { Set-KioskFileAcl $ConfigPath -UsersRead }
Set-KioskConfigAtomically $ConfigPath $candidateJson
Set-KioskFileAcl $ConfigPath -UsersRead
$journal.State.Method = $Method
Save-KioskRegistryJournal $journal
if ($Method -eq 'ShellLauncher') {
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher
    if ($feature.State -ne 'Enabled') {
        $result = Enable-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher -NoRestart
        if ($result.RestartNeeded) { throw 'Shell Launcher feature requires a restart. Restart Windows, then rerun this script; the current shell has not been changed.' }
    }
    if (-not $journal.State.Launcher) {
        $before = Invoke-Launcher 'GetDefaultShell'
        $enabled = Invoke-Launcher 'IsEnabled'
        $adminState = Get-LauncherAdmin
        $journal.State.Launcher = @{ Shell = $before.Shell; DefaultAction = $before.DefaultAction; Enabled = $enabled.Enabled; Admin = $adminState }
    }
    $journal.State.AppliedShell = $shellCommand
    $journal.State.AppliedAdmin = @{ Shell = 'explorer.exe'; DefaultAction = 0; CustomReturnCodes = @(); CustomReturnCodesAction = @() }
    $journal.State.AppliedEnabled = $true
    Save-KioskRegistryJournal $journal
    Invoke-Launcher 'SetDefaultShell' @{ Shell = $shellCommand; DefaultAction = 0 } | Out-Null
    Invoke-Launcher 'SetCustomShell' @{ Sid = 'S-1-5-32-544'; Shell = 'explorer.exe'; DefaultAction = 0 } | Out-Null
    Invoke-Launcher 'SetEnabled' @{ Enabled = $true } | Out-Null
    $journal.State.AppliedAdmin = Get-LauncherAdmin
    $journal.State.AppliedEnabled = (Invoke-Launcher 'IsEnabled').Enabled
    Save-KioskRegistryJournal $journal
} else {
    Invoke-DefaultUserHive { param($key) Set-KioskRegistryValue $script:journal $key 'Shell' $shellCommand 'String' }
}
Set-Value $system 'dontdisplaylastusername' 1
Set-Value $system 'HideFastUserSwitching' 0
Set-Value $winlogon 'ScRemoveOption' '0' 'String'
if ($RequireSmartCard) { Set-Value $system 'scforceoption' 1 }
Write-Output "Kiosk shell configured for future sign-ins: $shellCommand"
Write-Output 'Existing Windows sessions were not stopped. Validate a non-administrator card sign-in before wider deployment.'

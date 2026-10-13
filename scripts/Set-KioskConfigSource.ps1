<#
.SYNOPSIS
Konfiguracja z serwera: sets the folder or share the Kiosk downloads its configuration from at every start,
and optionally the account used to read that share.

.DESCRIPTION
Run as administrator, on a PC where the Kiosk is installed (Install-KioskShell.ps1) or after it.
The shared configuration C:\ProgramData\Kiosk\client.json gets ConfigSource; the Kiosk Card service then copies
the folder (client.json, logo, kiosk-news.json, ...) into C:\ProgramData\Kiosk\Central at every Kiosk start.

  * Domain PC: the service reads the share as the computer account (DOMAIN\PC$). Give "Domain Computers"
    (or the PCs' group) read access to the share and NTFS folder; no password needed.
  * Otherwise (Entra ID only, workgroup, NAS): -Credential (Get-Credential) saves an account for the share,
    encrypted for this computer (DPAPI) in C:\ProgramData\Kiosk\ConfigSource.cred, readable only by SYSTEM and
    administrators. Use an account that can only read that share.
  * -ConfigSource "" switches back to the PC's own configuration; -RemoveCredential deletes the saved account.

.EXAMPLE
.\Set-KioskConfigSource.ps1 -ConfigSource \\serwer\kiosk
.\Set-KioskConfigSource.ps1 -ConfigSource \\nas\kiosk -Credential (Get-Credential NAS\kiosk-read)
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigSource,
    [pscredential]$Credential,
    [switch]$RemoveCredential,
    [string]$ConfigPath = (Join-Path $env:ProgramData 'Kiosk\client.json')
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
$credentialFile = Join-Path (Split-Path $ConfigPath) 'ConfigSource.cred'

if ($PSBoundParameters.ContainsKey('ConfigSource')) {
    $ConfigSource = $ConfigSource.Trim().Trim('"')
    if ($ConfigSource -and -not [IO.Path]::IsPathRooted($ConfigSource)) { throw "Podaj pełną ścieżkę, np. \\serwer\kiosk: $ConfigSource" }
    if (-not (Test-Path $ConfigPath)) { throw "Brak konfiguracji Kiosku: $ConfigPath (najpierw Install-KioskShell.ps1)" }
    if ($PSCmdlet.ShouldProcess($ConfigPath, "ConfigSource = '$ConfigSource'")) {
        $json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        $json | Add-Member -NotePropertyName ConfigSource -NotePropertyValue $ConfigSource -Force
        $json | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding utf8
    }
}

if ($RemoveCredential -and (Test-Path $credentialFile) -and $PSCmdlet.ShouldProcess($credentialFile, 'delete')) {
    Remove-Item $credentialFile -Force
}

if ($Credential -and $PSCmdlet.ShouldProcess($credentialFile, "save account $($Credential.UserName)")) {
    Add-Type -AssemblyName System.Security
    $plain = @{ User = $Credential.UserName; Password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($plain)
    $blob = [Security.Cryptography.ProtectedData]::Protect($bytes, [Text.Encoding]::UTF8.GetBytes('Kiosk.ConfigSource'),
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    [Array]::Clear($bytes, 0, $bytes.Length)
    if (-not (Test-Path $credentialFile)) { New-Item -ItemType File -Path $credentialFile | Out-Null }
    # Only SYSTEM (the Kiosk Card service) and administrators may read it: DPAPI LocalMachine alone would let any local user decrypt it.
    & icacls.exe $credentialFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $credentialFile" }
    [IO.File]::WriteAllBytes($credentialFile, $blob)
}

$current = (Get-Content $ConfigPath -Raw | ConvertFrom-Json).ConfigSource
Write-Output ("Konfiguracja z serwera: " + $(if ($current) { $current } else { '(brak - konfiguracja tego komputera)' }))
Write-Output ("Konto do serwera: " + $(if (Test-Path $credentialFile) { 'zapisane' } else { 'konto komputera (DOMENA\KOMPUTER$)' }))
Write-Output 'Kiosk pobierze konfigurację przy następnym uruchomieniu.'

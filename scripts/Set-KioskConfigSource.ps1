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
  * -AccountsKey (Read-Host -AsSecureString): the key of the shared accounts in accounts.kiosk (Set-KioskSharedAccount.ps1).
  * -ConfigSource "" switches back to the PC's own configuration; -RemoveCredential deletes the saved account.
  * -Test (also run after every change): can this account and the computer account (SYSTEM, as the service reads
    it) or the saved account reach the folder, and is its client.json there and readable? Nothing is copied.

.EXAMPLE
.\Set-KioskConfigSource.ps1 -ConfigSource \\serwer\kiosk
.\Set-KioskConfigSource.ps1 -ConfigSource \\nas\kiosk -Credential (Get-Credential NAS\kiosk-read)
.\Set-KioskConfigSource.ps1 -Test
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigSource,
    [pscredential]$Credential,
    [switch]$RemoveCredential,
    # Konta wspólne: the key of accounts.kiosk (Set-KioskSharedAccount.ps1); an empty one removes it.
    [securestring]$AccountsKey,
    [switch]$Test,
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
    # A fresh file owned by Administrators: one a user placed there first would keep its owner's right to change the permissions.
    Remove-Item $credentialFile -Force -ErrorAction SilentlyContinue
    New-Item -ItemType File -Path $credentialFile | Out-Null
    & icacls.exe $credentialFile /setowner '*S-1-5-32-544' /Q | Out-Null
    # Only SYSTEM (the Kiosk Card service) and administrators may read it: DPAPI LocalMachine alone would let any local user decrypt it.
    & icacls.exe $credentialFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $credentialFile" }
    [IO.File]::WriteAllBytes($credentialFile, $blob)
}

$keyFile = Join-Path (Split-Path $ConfigPath) 'AccountsKey.cred'
if ($AccountsKey -and $PSCmdlet.ShouldProcess($keyFile, 'save the key of the shared accounts')) {
    $keyText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($AccountsKey))
    if ($keyText.Length -eq 0) { Remove-Item $keyFile -Force -ErrorAction SilentlyContinue }
    else {
        Add-Type -AssemblyName System.Security
        $bytes = [Text.Encoding]::UTF8.GetBytes($keyText)
        $blob = [Security.Cryptography.ProtectedData]::Protect($bytes, [Text.Encoding]::UTF8.GetBytes('Kiosk.SharedAccounts'),
            [Security.Cryptography.DataProtectionScope]::LocalMachine)
        [Array]::Clear($bytes, 0, $bytes.Length)
        Remove-Item $keyFile -Force -ErrorAction SilentlyContinue
        New-Item -ItemType File -Path $keyFile | Out-Null
        & icacls.exe $keyFile /setowner '*S-1-5-32-544' /Q | Out-Null
        & icacls.exe $keyFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed for $keyFile" }
        [IO.File]::WriteAllBytes($keyFile, $blob)
    }
}

$current = (Get-Content $ConfigPath -Raw | ConvertFrom-Json).ConfigSource
Write-Output ("Konfiguracja z serwera: " + $(if ($current) { $current } else { '(brak - konfiguracja tego komputera)' }))
Write-Output ("Konto do serwera: " + $(if (Test-Path $credentialFile) { 'zapisane' } else { 'konto komputera (DOMENA\KOMPUTER$)' }))
Write-Output ("Klucz kont wspólnych: " + $(if (Test-Path $keyFile) { 'zapisany' } else { 'brak' }))
Write-Output 'Kiosk pobierze konfigurację przy następnym uruchomieniu.'

if (-not $current -or $WhatIfPreference -or -not ($Test -or $PSBoundParameters.ContainsKey('ConfigSource') -or $Credential)) { return }

# The same check for every account: folder reachable and client.json present with valid JSON.
$check = {
    param($source)
    try {
        if (-not (Test-Path -LiteralPath $source)) { return 'NIE - brak dostępu do folderu' }
        $file = Join-Path $source 'client.json'
        if (-not (Test-Path -LiteralPath $file)) { return 'NIE - folder dostępny, ale nie ma w nim client.json' }
        $null = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        'OK - client.json czytelny'
    } catch { 'NIE - ' + $_.Exception.Message }
}
Write-Output ''
Write-Output "Test dostępu do $current"
Write-Output ("  Twoje konto ($env:USERDOMAIN\$env:USERNAME): " + (& $check $current))

if ((Test-Path $credentialFile) -and $current.StartsWith('\\')) {
    # The saved account, the way the service uses it (a connection to \\server\share).
    Add-Type -AssemblyName System.Security
    $account = [pscustomobject]@{ User = '?' }
    try {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credentialFile),
            [Text.Encoding]::UTF8.GetBytes('Kiosk.ConfigSource'), [Security.Cryptography.DataProtectionScope]::LocalMachine)
        $account = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        [Array]::Clear($bytes, 0, $bytes.Length)
        $parts = $current.TrimStart('\').Split('\', [StringSplitOptions]::RemoveEmptyEntries)
        $share = '\\' + $parts[0] + '\' + $parts[1]
        $secure = ConvertTo-SecureString $account.Password -AsPlainText -Force
        $drive = New-PSDrive -Name KioskTest -PSProvider FileSystem -Root $share -Credential ([pscredential]::new($account.User, $secure))
        try { $result = & $check $current } finally { Remove-PSDrive $drive -Force }
    } catch { $result = 'NIE - ' + $_.Exception.Message }
    Write-Output ("  Zapisane konto ($($account.User)): " + $result)
} else {
    # The computer account: SYSTEM is DOMAIN\PC$ on the network, as the Kiosk Card service.
    $out = Join-Path $env:windir ('Temp\kiosk-config-test-' + [guid]::NewGuid().ToString('N') + '.txt')
    $script = "`$s = '" + $current.Replace("'", "''") + "'; & {" + $check.ToString() + "} `$s | Out-File -LiteralPath '" + $out + "' -Encoding utf8"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $name = 'KioskConfigTest-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded"
        Register-ScheduledTask -TaskName $name -Action $action -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        Start-ScheduledTask -TaskName $name
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path $out) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
        Start-Sleep -Milliseconds 500
        $result = if (Test-Path $out) { (Get-Content $out -Raw).Trim() } else { 'NIE - test nie skończył się w 60 s' }
    } catch { $result = 'NIE - ' + $_.Exception.Message }
    finally {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item $out -Force -ErrorAction SilentlyContinue
    }
    Write-Output ("  Konto komputera ($env:USERDOMAIN\$env:COMPUTERNAME`$, tak pobiera Kiosk): " + $result)
    if ($result -notlike 'OK*') { Write-Output '  W domenie: daj grupie "Komputery domeny" odczyt udziału i folderu. Bez domeny: -Credential (Get-Credential).' }
}

<#
.SYNOPSIS
Konta wspólne: adds, changes or removes an account in accounts.kiosk, the file of shared accounts next to client.json.

.DESCRIPTION
The Kiosk uses these accounts top-down, chosen by name in Konfiguracja:
  * Akcje stron → "Zaloguj kontem wspólnym": the Kiosk fills in login and password on that page.
  * Aplikacje → "Konto wspólne do sieci": the program runs as usual and opens servers and shares as that account
    (like runas /netonly), e.g. a program lying on a file server.

Each account is encrypted with the key of the accounts (AES-256 + HMAC-SHA256, PBKDF2 of the key). Run this where
you edit the configuration (the server folder of Konfiguracja z serwera, or C:\ProgramData\Kiosk); the file is safe
to copy, but without the key it is useless. Every PC gets the same key once, encrypted for itself and readable only
by the Kiosk Card service and administrators: Install-KioskShell.ps1 -AccountsKey, Set-KioskConfigSource.ps1
-AccountsKey, or Konfiguracja → Aktualizacja → "Klucz kont wspólnych".

.EXAMPLE
.\Set-KioskSharedAccount.ps1 -Folder \\serwer\kiosk -Name portal -Credential (Get-Credential FIRMA\kiosk-portal)
.\Set-KioskSharedAccount.ps1 -Folder \\serwer\kiosk -Name portal -Remove
.\Set-KioskSharedAccount.ps1 -Folder \\serwer\kiosk -List
#>
[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(Mandatory)][string]$Folder,
    [Parameter(Mandatory, ParameterSetName = 'Set')][Parameter(Mandatory, ParameterSetName = 'Remove')][string]$Name,
    [Parameter(Mandatory, ParameterSetName = 'Set')][pscredential]$Credential,
    [Parameter(ParameterSetName = 'Remove')][switch]$Remove,
    [Parameter(ParameterSetName = 'List')][switch]$List,
    # The key of the accounts; asked for when not given. The same on every PC.
    [securestring]$Key
)
$ErrorActionPreference = 'Stop'
$file = Join-Path $Folder 'accounts.kiosk'
$data = if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$accounts = [ordered]@{}
if ($data -and $data.Accounts) { foreach ($p in $data.Accounts.PSObject.Properties) { $accounts[$p.Name] = $p.Value } }

if ($List) {
    if ($accounts.Count -eq 0) { Write-Output "Brak kont w $file" } else { $accounts.Keys | ForEach-Object { Write-Output $_ } }
    return
}
if ($Name.Trim().Length -eq 0 -or $Name -match '[;"]') { throw 'Podaj nazwę konta bez ; i ", np. portal.' }
$Name = $Name.Trim()

if ($Remove) {
    $existing = @($accounts.Keys | Where-Object { $_ -ieq $Name })
    if ($existing.Count -eq 0) { Write-Output "Nie ma konta $Name w $file"; return }
    foreach ($k in $existing) { $accounts.Remove($k) }
} else {
    if (-not $Key) { $Key = Read-Host 'Klucz kont wspólnych' -AsSecureString }
    $keyText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Key))
    if ($keyText.Length -lt 12) { throw 'Klucz kont wspólnych musi mieć co najmniej 12 znaków.' }
    $iterations = if ($data) { [int]$data.Iterations } else { 200000 }
    $salt = if ($data) { [Convert]::FromBase64String($data.Salt) } else { $s = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($s); $s }
    $keyBytes = [Text.Encoding]::UTF8.GetBytes($keyText)
    $derive = New-Object Security.Cryptography.Rfc2898DeriveBytes($keyBytes, $salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
    $material = $derive.GetBytes(64)
    $derive.Dispose()
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    $hmac = New-Object Security.Cryptography.HMACSHA256 (, [byte[]]$material[32..63])
    function Get-Mac([string]$n, [byte[]]$iv, [byte[]]$cipher) {
        [byte[]]$all = [Text.Encoding]::UTF8.GetBytes($n.ToLowerInvariant()) + $iv + $cipher
        $hmac.ComputeHash($all)
    }
    # The same key for every account: an existing account must open with it.
    foreach ($k in $accounts.Keys) {
        $e = $accounts[$k]
        $mac = Get-Mac $k ([Convert]::FromBase64String($e.Iv)) ([Convert]::FromBase64String($e.Data))
        if ([Convert]::ToBase64String($mac) -ne $e.Mac) { throw "Ten klucz nie pasuje do kont w $file (konto $k). Podaj klucz, którym je zapisano." }
        break
    }
    $aes = [Security.Cryptography.Aes]::Create()
    $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'
    $aes.Key = [byte[]]$material[0..31]
    $aes.GenerateIV()
    $plain = [Text.Encoding]::UTF8.GetBytes((@{ User = $Credential.UserName; Password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress))
    $cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)
    [Array]::Clear($plain, 0, $plain.Length)
    foreach ($k in @($accounts.Keys | Where-Object { $_ -ieq $Name })) { $accounts.Remove($k) }
    $accounts[$Name] = [ordered]@{ Iv = [Convert]::ToBase64String($aes.IV); Data = [Convert]::ToBase64String($cipher); Mac = [Convert]::ToBase64String((Get-Mac $Name $aes.IV $cipher)) }
    $aes.Dispose(); $hmac.Dispose()
    [Array]::Clear($material, 0, $material.Length)
    $data = [pscustomobject]@{ Version = 1; Salt = [Convert]::ToBase64String($salt); Iterations = $iterations }
}

$json = [ordered]@{ Version = 1; Salt = $data.Salt; Iterations = [int]$data.Iterations; Accounts = $accounts } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($file, $json, (New-Object Text.UTF8Encoding $false))
Write-Output ("Zapisano $file, konta: " + $(if ($accounts.Count) { ($accounts.Keys -join ', ') } else { '(brak)' }))
Write-Output 'Komputery z Konfiguracją z serwera pobiorą plik przy następnym uruchomieniu Kiosku.'

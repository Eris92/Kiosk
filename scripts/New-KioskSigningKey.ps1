#Requires -Version 5.1
<#
.SYNOPSIS
Creates the key GitHub signs Kiosk packages with. Run once, on the owner's own PC (not on a kiosk PC).

.DESCRIPTION
  * The private key goes to the clipboard: paste it in GitHub as the repository secret KIOSK_SIGNING_KEY
    (https://github.com/Eris92/Kiosk/settings/secrets/actions). It is not saved anywhere else.
  * The public key is printed: it is built into the Kiosk (Updater.cs, SigningKey), which then installs only
    packages signed with this key.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Core
$parameters = New-Object System.Security.Cryptography.CngKeyCreationParameters
$parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
$key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $null, $parameters)
try {
    $private = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    # BCRYPT_ECCKEY_BLOB: 8-byte header, then X and Y (32 bytes each); wrapped as SubjectPublicKeyInfo (P-256).
    $blob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $prefix = [byte[]](0x30,0x59,0x30,0x13,0x06,0x07,0x2a,0x86,0x48,0xce,0x3d,0x02,0x01,0x06,0x08,0x2a,0x86,0x48,0xce,0x3d,0x03,0x01,0x07,0x03,0x42,0x00,0x04)
    $public = $prefix + $blob[8..71]
    function Pem([string]$Label, [byte[]]$Bytes) {
        $text = [Convert]::ToBase64String($Bytes)
        $lines = for ($i = 0; $i -lt $text.Length; $i += 64) { $text.Substring($i, [Math]::Min(64, $text.Length - $i)) }
        (@("-----BEGIN $Label-----") + $lines + @("-----END $Label-----")) -join "`n"
    }
    Set-Clipboard -Value (Pem 'PRIVATE KEY' $private)
    Write-Output 'Klucz prywatny jest w schowku: wklej go w GitHub jako sekret KIOSK_SIGNING_KEY i nie zapisuj nigdzie indziej.'
    Write-Output ''
    Write-Output 'Klucz publiczny (wyślij go w wątku Kiosku):'
    Write-Output (Pem 'PUBLIC KEY' $public)
}
finally { $key.Dispose() }

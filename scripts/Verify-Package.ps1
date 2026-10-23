#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..'),
    [switch]$RequireSigned,
    [string]$TrustedSignerThumbprint = ''
)
$ErrorActionPreference = 'Stop'
$thumbprint = ($TrustedSignerThumbprint -replace '\s', '').ToUpperInvariant()
if ($thumbprint -and $thumbprint -notmatch '^[A-F0-9]{40}$') { throw 'TrustedSignerThumbprint must be a 40-character certificate thumbprint.' }
$verifySignatures = [bool]$RequireSigned -or [bool]$thumbprint
$root = (Resolve-Path -LiteralPath $PackagePath).ProviderPath.TrimEnd('\')
$manifest = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.Format -ne 1 -or -not $manifest.Files) { throw 'Invalid Kiosk package manifest.' }
$listed = @()
foreach ($entry in $manifest.Files) {
    if (-not $entry.Path -or [IO.Path]::IsPathRooted($entry.Path) -or $entry.Path -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Invalid path in package manifest.' }
    $file = [IO.Path]::GetFullPath((Join-Path $root $entry.Path))
    if (-not $file.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escaped its root.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $($entry.Path)" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Package hash mismatch: $($entry.Path)" }
    if ($verifySignatures -and ($entry.Path -match '^client/Kiosk\.Client\.(exe|dll)$|^credential-provider/.*\.dll$|^scripts/.*\.ps1$')) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid') { throw "A trusted Authenticode signature is required: $($entry.Path)" }
        if ($thumbprint -and $signature.SignerCertificate.Thumbprint -ne $thumbprint) { throw "Unexpected Authenticode signer: $($entry.Path)" }
    }
    $listed += $entry.Path.Replace('/', '\')
}
$actual = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object { $_.FullName -ne (Join-Path $root 'package.json') } | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
if (@(Compare-Object $listed $actual).Count -ne 0) { throw 'Package contains unlisted, duplicated or missing files.' }
foreach ($required in @('client\Kiosk.Client.exe','client\Kiosk.Client.dll','client\client.json','credential-provider\KioskCredentialProvider.dll','config\client.json','scripts\Install-KioskShell.ps1','scripts\Install-KioskCardService.ps1','scripts\Install-KioskCredentialProvider.ps1','scripts\Set-KioskSignInScreen.ps1')) {
    if ($listed -notcontains $required) { throw "Package lacks required file: $required" }
}
$allowedConfig = @('config\client.json','config\client.example.json','config\intune\ShellLauncher.xml')
foreach ($file in $listed | Where-Object { $_ -like 'config\*' }) {
    if ($allowedConfig -notcontains $file) { throw "Unexpected configuration file in production package: $file" }
}
$clientConfig = Get-Content -LiteralPath (Join-Path $root 'client\client.json') -Raw -Encoding UTF8
$publicConfig = Get-Content -LiteralPath (Join-Path $root 'config\client.json') -Raw -Encoding UTF8
if ($clientConfig.Trim() -ne $publicConfig.Trim()) { throw 'Package client config differs from its public default.' }
$config = $clientConfig | ConvertFrom-Json
if ($config.ConfigVersion -ne 2 -or $config.SessionMode -ne 'WindowsShell' -or $config.TestMode -isnot [bool] -or $config.TestMode) {
    throw 'Production package requires ConfigVersion=2, SessionMode=WindowsShell and TestMode=false.'
}
Write-Output "Package inventory and SHA256 verified: $root"
if (-not $verifySignatures) { Write-Output 'Authenticode enforcement was not requested. Hashes verify consistency, not publisher authenticity.' }

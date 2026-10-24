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
function Assert-OrdinaryPackagePath([string]$Path) {
    # A hash of a junction/link target does not authenticate the link or
    # keep installation reads inside the extracted package.
    $probe = [IO.Path]::GetFullPath($Path)
    while ($probe) {
        $item = Get-Item -LiteralPath $probe -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package contains a junction or symbolic link: $probe" }
        $parent = [IO.Directory]::GetParent($probe)
        if ($null -eq $parent) { break }
        $probe = $parent.FullName
    }
}
$root = (Resolve-Path -LiteralPath $PackagePath).ProviderPath.TrimEnd('\')
Assert-OrdinaryPackagePath $root
$manifest = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.Format -ne 1 -or -not $manifest.Files) { throw 'Invalid Kiosk package manifest.' }
$listed = @()
foreach ($entry in $manifest.Files) {
    if (-not $entry.Path -or [IO.Path]::IsPathRooted($entry.Path) -or
        $entry.Path -match '[:\\]|(^|/)\.{1,2}(/|$)|//|/$') { throw 'Invalid path in package manifest.' }
    $file = [IO.Path]::GetFullPath((Join-Path $root $entry.Path))
    if (-not $file.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escaped its root.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $($entry.Path)" }
    Assert-OrdinaryPackagePath $file
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Package hash mismatch: $($entry.Path)" }
    $ownedFile = $entry.Path -match '^client/Kiosk\.Client\.(exe|dll)$|^credential-provider/KioskCredentialProvider\.dll$|^scripts/.*\.ps1$'
    if ($verifySignatures -and ($ownedFile -or $entry.Path -match '\.(exe|dll)$')) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid') { throw "A trusted Authenticode signature is required: $($entry.Path)" }
        if ($thumbprint -and $ownedFile -and $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
            throw "Unexpected Authenticode signer: $($entry.Path)"
        }
    }
    $relative = $entry.Path.Replace('/', '\')
    if ($listed -contains $relative) { throw "Duplicate package path: $($entry.Path)" }
    $listed += $relative
}
# Walk one directory at a time: never descend through an unlisted junction
# before checking its attributes, and include hidden/system files.
$actual = New-Object 'System.Collections.Generic.List[string]'
$pending = New-Object 'System.Collections.Generic.Queue[string]'
$pending.Enqueue($root)
while ($pending.Count -gt 0) {
    $directory = $pending.Dequeue()
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package contains a junction or symbolic link: $($item.FullName)" }
        if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        elseif ($item.FullName -ne (Join-Path $root 'package.json')) { $actual.Add($item.FullName.Substring($root.Length + 1)) }
    }
}
if (@(Compare-Object $listed $actual.ToArray()).Count -ne 0) { throw 'Package contains unlisted, duplicated or missing files.' }
foreach ($file in $listed) {
    if ($file -ne 'README.md' -and $file -notmatch '^(client|credential-provider|config|scripts)\\') {
        throw "Unexpected top-level package file: $file"
    }
}
foreach ($required in @('client\Kiosk.Client.exe','client\Kiosk.Client.dll','client\client.json','credential-provider\KioskCredentialProvider.dll','config\client.json','scripts\Install-KioskShell.ps1','scripts\Install-KioskCardService.ps1','scripts\Install-KioskCredentialProvider.ps1','scripts\Set-KioskSignInScreen.ps1')) {
    if ($listed -notcontains $required) { throw "Package lacks required file: $required" }
}
$allowedConfig = @('config\client.json','config\client.example.json','config\intune\ShellLauncher.xml')
foreach ($file in $listed | Where-Object { $_ -like 'config\*' }) {
    if ($allowedConfig -notcontains $file) { throw "Unexpected configuration file in production package: $file" }
}
$allowedScripts = @('Deployment.Common.ps1','Install-KioskShell.ps1','Install-KioskCardService.ps1',
    'Install-KioskCredentialProvider.ps1','Install-KioskConfig.ps1','KioskRuntime.ps1',
    'Set-KioskConfigSource.ps1','Set-KioskKeyboardLock.ps1','Set-KioskSharedAccount.ps1',
    'Set-KioskSignInScreen.ps1','Test-Prerequisites.ps1','Verify-Package.ps1')
foreach ($file in $listed | Where-Object { $_ -like 'scripts\*' }) {
    if ($file.Substring('scripts\'.Length) -notin $allowedScripts) { throw "Unexpected deployment script: $file" }
}
foreach ($file in $listed | Where-Object { $_ -like 'credential-provider\*' }) {
    if ($file -ne 'credential-provider\KioskCredentialProvider.dll') { throw "Unexpected credential-provider file: $file" }
}
foreach ($file in $listed | Where-Object { $_ -like 'client\*' }) {
    if ($file -match '\.(exe|com|scr|ps1|cmd|bat|vbs|js|jse|wsf|hta)$' -and
        $file -notin @('client\Kiosk.Client.exe','client\createdump.exe',
            'client\KioskKonfiguracje.exe','client\news-example\KioskKomunikaty.exe','client\fido\FidoRun.exe')) {
        throw "Unexpected executable in client payload: $file"
    }
}
$clientConfig = Get-Content -LiteralPath (Join-Path $root 'client\client.json') -Raw -Encoding UTF8
$publicConfig = Get-Content -LiteralPath (Join-Path $root 'config\client.json') -Raw -Encoding UTF8
if ($clientConfig.Trim() -ne $publicConfig.Trim()) { throw 'Package client config differs from its public default.' }
$config = $clientConfig | ConvertFrom-Json
if ($config.ConfigVersion -ne 5 -or $config.SessionMode -ne 'SelfService' -or $config.TestMode -isnot [bool] -or $config.TestMode) {
    throw 'Production package requires ConfigVersion=5, SessionMode=SelfService and TestMode=false.'
}
Write-Output "Package inventory and SHA256 verified: $root"
if (-not $verifySignatures) { Write-Output 'Authenticode enforcement was not requested. Hashes verify consistency, not publisher authenticity.' }

#Requires -Version 5.1
<#
.SYNOPSIS
Builds an isolated distributable package with public configuration, native DLL and SHA256 inventory.
.DESCRIPTION
Only allowlisted public configs are copied. -SigningCertificateThumbprint uses an organization's
installed code-signing certificate; -RequireSigned rejects builds without one.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [switch]$RequireSigned,
    [string]$SigningCertificateThumbprint = '',
    [string]$TimestampServer = ''
)
$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot '..\out\package' }
$certificate = $null
$thumbprint = ''
if ($SigningCertificateThumbprint) {
    $thumbprint = ($SigningCertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[A-F0-9]{40}$') { throw 'SigningCertificateThumbprint must be a 40-character certificate thumbprint.' }
    $certificate = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My' |
        Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey } |
        Select-Object -First 1)[0]
    if (-not $certificate) { throw 'Code-signing certificate with a private key was not found in CurrentUser or LocalMachine My store.' }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'Code-signing certificate is outside its validity period.' }
    $codeSigning = $false
    foreach ($extension in $certificate.Extensions) {
        if ($extension -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
            @($extension.EnhancedKeyUsages | ForEach-Object { $_.Value }) -contains '1.3.6.1.5.5.7.3.3') { $codeSigning = $true }
    }
    if (-not $codeSigning) { throw 'Certificate does not have the Code Signing enhanced key usage.' }
}
if ($RequireSigned -and -not $certificate) { throw 'New-Package rebuilds binaries. Supply -SigningCertificateThumbprint to produce a signed package.' }
if ($TimestampServer -and -not $certificate) { throw 'TimestampServer requires SigningCertificateThumbprint.' }
if ($TimestampServer -and $TimestampServer -notmatch '^http://') { throw 'PowerShell Authenticode timestamp servers must use http://.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$outRoot = Join-Path $root 'out'
$destination = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\')
if (-not $destination.StartsWith($outRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package OutputPath must be a dedicated directory below this repository\out.' }
$revision = (& git -C $root rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $revision = 'unavailable' }
$dirty = (& git -C $root status --porcelain --untracked-files=all 2>$null)
if ($certificate -and ($revision -eq 'unavailable' -or $dirty)) { throw 'A signed release requires a clean Git worktree and a known source revision.' }
$stage = Join-Path $outRoot ('.kiosk-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    & (Join-Path $PSScriptRoot 'Build.ps1') -OutputPath (Join-Path $stage 'client') -ConfigPath (Join-Path $root 'config\client.json')
    & (Join-Path $PSScriptRoot 'Build-CredentialProvider.ps1') -OutputPath (Join-Path $stage 'credential-provider')
    # Native intermediate files and symbols are not runtime payloads.
    Get-ChildItem -LiteralPath (Join-Path $stage 'credential-provider') -File | Where-Object { $_.Extension -ne '.dll' } | Remove-Item -Force
    $configDirectory = Join-Path $stage 'config'
    New-Item -ItemType Directory -Path (Join-Path $configDirectory 'intune') -Force | Out-Null
    foreach ($name in @('client.json', 'client.example.json')) {
        Copy-Item -LiteralPath (Join-Path $root "config\$name") -Destination $configDirectory
    }
    Copy-Item -LiteralPath (Join-Path $root 'config\intune\ShellLauncher.xml') -Destination (Join-Path $configDirectory 'intune')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $stage 'README.md')
    $scripts = Join-Path $stage 'scripts'
    New-Item -ItemType Directory -Path $scripts | Out-Null
    foreach ($name in @('Deployment.Common.ps1','Install-KioskShell.ps1','Install-KioskCardService.ps1',
        'Install-KioskCredentialProvider.ps1','Install-KioskConfig.ps1',
        'Set-KioskConfigSource.ps1','Set-KioskKeyboardLock.ps1','Set-KioskSharedAccount.ps1',
        'Set-KioskSignInScreen.ps1','Test-Prerequisites.ps1','Verify-Package.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scripts
    }
    if ($certificate) {
        $ownedFiles = @(
            (Join-Path $stage 'client\Kiosk.Client.exe'),
            (Join-Path $stage 'client\Kiosk.Client.dll'),
            (Join-Path $stage 'client\KioskKonfiguracje.exe'),
            (Join-Path $stage 'client\news-example\KioskKomunikaty.exe'),
            (Join-Path $stage 'client\news-example\KioskKomunikaty.dll'),
            (Join-Path $stage 'client\fido\FidoRun.exe'),
            (Join-Path $stage 'credential-provider\KioskCredentialProvider.dll')
        ) + @(Get-ChildItem -LiteralPath $scripts -Filter '*.ps1' -File | ForEach-Object { $_.FullName })
        foreach ($file in $ownedFiles) {
            $parameters = @{ LiteralPath = $file; Certificate = $certificate; HashAlgorithm = 'SHA256'; ErrorAction = 'Stop' }
            if ($TimestampServer) { $parameters.TimestampServer = $TimestampServer }
            $result = Set-AuthenticodeSignature @parameters
            if ($result.Status -ne 'Valid' -or $result.SignerCertificate.Thumbprint -ne $thumbprint) { throw "Code signing failed or signer was untrusted: $file ($($result.Status))." }
        }
    }
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1).Replace('\','/')
        [ordered]@{Path=$relative;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $stage 'client\Kiosk.Client.exe')
    [ordered]@{Format=1;SourceRevision=$revision;SourceHasUncommittedChanges=[bool]$dirty;Runtime='win-x64 self-contained';ClientAuthenticode=[string]$signature.Status;SignerThumbprint=[string]$signature.SignerCertificate.Thumbprint;Configuration='public-default';Files=$files} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'package.json') -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $stage -RequireSigned:([bool]$RequireSigned -or [bool]$certificate) -TrustedSignerThumbprint $thumbprint
    if (Test-Path -LiteralPath $destination) { throw "Package output already exists; choose a new directory to preserve the previous artifact: $destination" }
    Move-Item -LiteralPath $stage -Destination $destination
    Write-Output "Distributable package ready: $destination"
} finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    if ((Split-Path -Parent $resolved) -ne $outRoot -or (Split-Path -Leaf $resolved) -notmatch '^\.kiosk-package-[a-f0-9]{32}$') { throw 'Unexpected package cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

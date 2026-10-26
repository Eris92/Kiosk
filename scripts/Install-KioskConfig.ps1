#Requires -Version 5.1
<#
.SYNOPSIS
Installs the configuration editor on an administrator's PC without changing its Windows shell.
.DESCRIPTION
Stages each client version in a new directory, validates the selected configuration, and changes
only the editor/preview shortcuts. An existing configuration and older payloads are retained.
-Uninstall removes only shortcuts created by this installer; it retains the configuration and payloads.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk\ConfigEditor'),
    [string]$ConfigPath = (Join-Path $env:ProgramData 'Kiosk\client.json'),
    [string]$SourceConfig = '',
    [switch]$NoDesktopShortcuts,
    [switch]$RequireSigned,
    [string]$TrustedSignerThumbprint = '',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
Assert-KioskAdministrator
if (-not $SourceConfig) { $SourceConfig = Join-Path $PSScriptRoot '..\config\client.json' }
$InstallPath = Assert-KioskProgramPath $InstallPath
$ConfigPath = Assert-KioskDataFilePath $ConfigPath '.json'

# This tool must not take ownership of the shell or card-service directories.
$sharedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Kiosk')).TrimEnd('\')
foreach ($reserved in @($sharedRoot, (Join-Path $sharedRoot 'ClientVersions'), (Join-Path $sharedRoot 'CardService'))) {
    if ([string]::Equals($InstallPath, $reserved, [StringComparison]::OrdinalIgnoreCase) -or
        (-not [string]::Equals($reserved, $sharedRoot, [StringComparison]::OrdinalIgnoreCase) -and
         $InstallPath.StartsWith($reserved + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw "Config editor InstallPath cannot use a shared Kiosk deployment directory: $InstallPath"
    }
}

$links = @(
    [pscustomobject]@{ Path = (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Kiosk - Edytor konfiguracji.lnk'); Admin = $true; Arguments = ('--settings "{0}"' -f $ConfigPath) },
    [pscustomobject]@{ Path = (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Kiosk - Podglad.lnk'); Admin = $false; Arguments = ('"{0}"' -f $ConfigPath) },
    [pscustomobject]@{ Path = (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Kiosk - Edytor konfiguracji.lnk'); Admin = $true; Arguments = ('--settings "{0}"' -f $ConfigPath) },
    [pscustomobject]@{ Path = (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Kiosk - Podglad.lnk'); Admin = $false; Arguments = ('"{0}"' -f $ConfigPath) }
)
$shell = New-Object -ComObject WScript.Shell
function Test-OwnedShortcut([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $target = $shell.CreateShortcut($Path).TargetPath
    if (-not $target.StartsWith($InstallPath + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    return $target.Substring($InstallPath.Length + 1) -match '^release-[a-f0-9]{32}\\Kiosk\.Client\.exe$'
}

if ($Uninstall) {
    foreach ($link in $links) {
        if (-not (Test-Path -LiteralPath $link.Path -PathType Leaf)) { continue }
        if (-not (Test-OwnedShortcut $link.Path)) { Write-Warning "Skipping a shortcut not owned by this installer: $($link.Path)"; continue }
        if ($PSCmdlet.ShouldProcess($link.Path, 'remove configuration-editor shortcut')) {
            Remove-Item -LiteralPath $link.Path -Force
        }
    }
    if ($WhatIfPreference) { Write-Output 'WhatIf: configuration-editor shortcuts would be removed; configuration and versioned program files retained.' }
    else { Write-Output 'Configuration-editor shortcuts removed; configuration and versioned program files retained.' }
    return
}

if (-not $SourcePath) { $SourcePath = Get-KioskClientSource }
$source = (Resolve-Path -LiteralPath $SourcePath).ProviderPath
$packageHashes = Assert-KioskPackagedSource $source 'client' ([bool]$RequireSigned) $TrustedSignerThumbprint
$sourceExe = Join-Path $source 'Kiosk.Client.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $source 'hostfxr.dll') -PathType Leaf)) {
    throw 'Install-KioskConfig requires a published self-contained Kiosk client.'
}
$configDirectory = Split-Path -Parent $ConfigPath
foreach ($path in @($configDirectory, $ConfigPath)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Configuration path contains a junction or symbolic link: $path"
    }
}
$selectedConfig = Get-KioskInstallConfigSource $ConfigPath $SourceConfig $false
Assert-KioskClientConfig $sourceExe $selectedConfig
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$installLinks = if ($NoDesktopShortcuts) { @($links | Where-Object { -not $_.Path.StartsWith($desktop + '\', [StringComparison]::OrdinalIgnoreCase) }) } else { $links }
foreach ($link in $installLinks) {
    if ((Test-Path -LiteralPath $link.Path -PathType Leaf) -and -not (Test-OwnedShortcut $link.Path)) {
        throw "Shortcut belongs to another installation: $($link.Path)"
    }
}
$release = 'release-' + [Guid]::NewGuid().ToString('N')
$target = Join-Path $InstallPath $release
if (-not $PSCmdlet.ShouldProcess($target, 'stage a verified client and update editor shortcuts')) { return }

Set-KioskDirectoryAcl $InstallPath -UsersRead
New-Item -ItemType Directory -Path $target | Out-Null
Copy-KioskPayloadVerified $source $target $packageHashes
$exe = Join-Path $target 'Kiosk.Client.exe'
Assert-KioskClientConfig $exe $selectedConfig
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Set-KioskDirectoryAcl $configDirectory -UsersRead
    Set-KioskConfigAtomically $ConfigPath (Get-Content -LiteralPath $selectedConfig -Raw -Encoding UTF8)
    Set-KioskFileAcl $ConfigPath -UsersRead
}

$previous = @{}
try {
    foreach ($link in $installLinks) {
        $path = $link.Path
        $previous[$path] = if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::ReadAllBytes($path) } else { $null }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $shortcut = $shell.CreateShortcut($path)
        $shortcut.TargetPath = $exe
        $shortcut.Arguments = $link.Arguments
        $shortcut.WorkingDirectory = $target
        $shortcut.IconLocation = "$exe,0"
        $shortcut.Save()
        if ($link.Admin) {
            $bytes = [IO.File]::ReadAllBytes($path)
            $bytes[0x15] = $bytes[0x15] -bor 0x20
            [IO.File]::WriteAllBytes($path, $bytes)
        }
    }
} catch {
    foreach ($path in $previous.Keys) {
        if ($null -eq $previous[$path]) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
        else { [IO.File]::WriteAllBytes($path, $previous[$path]) }
    }
    throw
}
Write-Output "Configuration editor ready: $exe (configuration: $ConfigPath)"

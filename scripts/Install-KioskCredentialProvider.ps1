<#
.SYNOPSIS
Installs the Kiosk sign-in tile: the Windows security key (FIDO2) sign-in with the Kiosk picture and title.

.DESCRIPTION
Run as administrator after scripts\Build-CredentialProvider.ps1. Supports -WhatIf; -Uninstall reverts it.

  * Copies KioskCredentialProvider.dll to -InstallPath and registers it as a credential provider.
  * Draws the tile picture (-TileImage) in the Kiosk colours.
  * Existing Windows sign-in options remain available. -ReplaceWindowsSecurityKey and -MakeDefault are opt-in.
  * -HideOtherOptions also hides the password and Windows Hello PIN tiles. Only after a card sign-in works and
    another administrator can sign in with a card; local password accounts can no longer sign in.

If the sign-in screen misbehaves: sign in with a password (it stays unless -HideOtherOptions) and run -Uninstall,
Legacy installations without saved state require manual registry recovery; see README.md.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = '',
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk\SignIn'),
    [string]$TileImage = (Join-Path $env:ProgramData 'Kiosk\tile.bmp'),
    [string]$Title = 'Przyłóż kartę',
    [string]$AccentColor = '#3DDC84',
    [switch]$ReplaceWindowsSecurityKey,
    [switch]$MakeDefault,
    [switch]$HideOtherOptions,
    [switch]$Trace, # Writes C:\ProgramData\Kiosk\signin-trace.log for diagnosing the tile.
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
Assert-KioskAdministrator
$InstallPath = Assert-KioskProgramPath $InstallPath
$TileImage = Assert-KioskDataFilePath $TileImage '.bmp'
$journal = $null
function Write-Setting($Path, $Name, $Value, $Kind = 'String', [switch]$CommaSet) {
    if ($PSCmdlet.ShouldProcess("$Path\$Name", 'write Kiosk setting (previous value retained)')) {
        if (-not $script:journal) { $script:journal = Open-KioskRegistryJournal 'credential-provider' }
        Set-KioskRegistryValue $script:journal $Path $Name $Value $Kind -CommaSet:$CommaSet
    }
}
$clsid = '{7A4D2C1E-5B3F-4E8A-9C6D-2F1B8E4A7C30}'
$fido = '{F8A1793B-7873-4046-B2A7-1F318747F427}'
$others = '{60b78e88-ead8-445c-9cfd-0b87f74ea6cd}', '{D6886603-9D2F-4EB2-B667-1971041FA96B}' # password, Windows Hello
$providers = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers'
$com = "HKLM:\SOFTWARE\Classes\CLSID\$clsid"
$settings = 'HKLM:\SOFTWARE\Kiosk\CredentialProvider'
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'

function Set-Excluded([string[]]$Add) {
    $current = (Get-ItemProperty -Path $policy -Name ExcludedCredentialProviders -ErrorAction SilentlyContinue).ExcludedCredentialProviders
    $list = @($current -split ',' | Where-Object { $_ } | ForEach-Object { $_.Trim() })
    foreach ($item in $Add) { if ($list -notcontains $item) { $list += $item } }
    if ($Add.Count -gt 0) { Write-Setting $policy 'ExcludedCredentialProviders' ($list -join ',') 'String' -CommaSet }
}

if ($Uninstall) {
    $state = Join-Path $env:ProgramData 'Kiosk\DeploymentState\credential-provider.clixml'
    if (-not (Test-Path -LiteralPath $state)) { throw 'No saved deployment state exists. Do not guess previous sign-in policies; use the documented legacy recovery procedure.' }
    if ($PSCmdlet.ShouldProcess('Kiosk sign-in tile', 'restore the registry values recorded during installation')) {
        $journal = Open-KioskRegistryJournal 'credential-provider'
        Restore-KioskRegistryJournal $journal
        foreach ($path in @("$providers\$clsid", "$com\InprocServer32", $com, $settings)) {
            if (Test-Path -LiteralPath $path) {
                $key = Get-Item -LiteralPath $path
                try { $empty = $key.GetValueNames().Count -eq 0 -and $key.GetSubKeyNames().Count -eq 0 }
                finally { $key.Close() }
                if ($empty) { Remove-Item -LiteralPath $path }
            }
        }
    }
    Write-Output 'Kiosk tile settings restored where unchanged; DLL and image retained. Review any policy-conflict warnings.'
    return
}
if (-not $SourcePath) {
    $root = Split-Path -Parent $PSScriptRoot
    $SourcePath = Join-Path $root 'credential-provider\KioskCredentialProvider.dll'
    if (Test-Path -LiteralPath (Join-Path $root 'src\Kiosk.CredentialProvider')) { $SourcePath = Join-Path $root 'out\credential-provider\KioskCredentialProvider.dll' }
}
if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) { throw "Build the sign-in tile first (scripts\Build-CredentialProvider.ps1): $SourcePath" }
if (-not (Test-Path "$providers\$fido")) { throw "Windows security key provider $fido is not registered on this PC." }

# 1. Program file and tile picture.
$dll = Join-Path $InstallPath ('KioskCredentialProvider-' + [Guid]::NewGuid().ToString('N') + '.dll')
if ($PSCmdlet.ShouldProcess($dll, 'copy sign-in tile')) {
    Set-KioskDirectoryAcl $InstallPath -UsersRead
    # Never replace a DLL already loaded in LogonUI; update registration after copying a fresh version.
    Copy-Item -LiteralPath $SourcePath -Destination $dll
}
if ($PSCmdlet.ShouldProcess($TileImage, 'draw tile picture')) {
    Add-Type -AssemblyName System.Drawing
    Set-KioskDirectoryAcl (Split-Path -Parent $TileImage) -UsersRead
    if (Test-Path -LiteralPath $TileImage) { Set-KioskFileAcl $TileImage -UsersRead }
    $size = 448
    $bitmap = $null; $graphics = $null; $pen = $null; $card = $null; $brush = $null
    try {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.Clear([System.Drawing.Color]::FromArgb(24, 35, 54))
        $accent = [System.Drawing.ColorTranslator]::FromHtml($AccentColor)
        $pen = New-Object System.Drawing.Pen $accent, 16
        $card = New-Object System.Drawing.Drawing2D.GraphicsPath
        $x = 84; $y = 124; $w = 280; $h = 200; $r = 40
        $card.AddArc($x, $y, $r, $r, 180, 90); $card.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
        $card.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $card.AddArc($x, $y + $h - $r, $r, $r, 90, 90); $card.CloseFigure()
        $graphics.DrawPath($pen, $card)
        $brush = New-Object System.Drawing.SolidBrush $accent
        $graphics.FillRectangle($brush, $x + 46, $y + 58, 74, 58)
        $bitmap.Save($TileImage, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        foreach ($resource in @($brush, $card, $pen, $graphics, $bitmap)) {
            if ($null -ne $resource) { $resource.Dispose() }
        }
    }
    Set-KioskFileAcl $TileImage -UsersRead
}

# 2. Register after the DLL and image are in place. Windows' own sign-in options remain by default.
Write-Setting $com '' 'Kiosk sign-in tile'
Write-Setting "$com\InprocServer32" '' $dll
Write-Setting "$com\InprocServer32" 'ThreadingModel' 'Apartment'
Write-Setting $settings 'Title' $Title
Write-Setting $settings 'TileImage' $TileImage
Write-Setting $settings 'InnerProvider' $fido
Write-Setting $settings 'Trace' ([int][bool]$Trace) 'DWord'
Write-Setting "$providers\$clsid" '' 'Kiosk'
$hide = @()
if ($ReplaceWindowsSecurityKey) { $hide += $fido }
if ($HideOtherOptions) { $hide += $others }
Set-Excluded -Add $hide
if ($MakeDefault) { Write-Setting $policy 'DefaultCredentialProvider' $clsid }
if ($WhatIfPreference) { Write-Output "WhatIf: Kiosk sign-in tile would be registered: $dll" }
else {
    Write-Output "Kiosk sign-in tile registered: $dll"
    Write-Output 'Sign out and select the Kiosk card tile in Sign-in options. Existing Windows options are retained unless explicitly hidden.'
}

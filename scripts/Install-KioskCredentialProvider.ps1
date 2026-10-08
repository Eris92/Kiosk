<#
.SYNOPSIS
Installs the Kiosk sign-in tile: the Windows security key (FIDO2) sign-in with the Kiosk picture and title.

.DESCRIPTION
Run as administrator after scripts\Build-CredentialProvider.ps1. Supports -WhatIf; -Uninstall reverts it.

  * Copies KioskCredentialProvider.dll to -InstallPath and registers it as a credential provider.
  * Draws the tile picture (-TileImage) in the Kiosk colours.
  * Hides Windows' own security key tile, so the Kiosk tile replaces it (the Kiosk tile still uses it inside).
  * -HideOtherOptions also hides the password and Windows Hello PIN tiles. Only after a card sign-in works and
    another administrator can sign in with a card; local password accounts can no longer sign in.

If the sign-in screen misbehaves: sign in with a password (it stays unless -HideOtherOptions) and run -Uninstall,
or from Windows Recovery > Command Prompt load the SOFTWARE hive and delete the provider key named below.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\out\credential-provider\KioskCredentialProvider.dll'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Kiosk\SignIn'),
    [string]$TileImage = (Join-Path $env:ProgramData 'Kiosk\tile.bmp'),
    [string]$Title = 'Przyłóż kartę',
    [string]$AccentColor = '#3DDC84',
    [switch]$HideOtherOptions,
    [switch]$Trace, # Writes C:\ProgramData\Kiosk\signin-trace.log for diagnosing the tile.
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }

$clsid = '{7A4D2C1E-5B3F-4E8A-9C6D-2F1B8E4A7C30}'
$fido = '{F8A1793B-7873-4046-B2A7-1F318747F427}'
$others = '{60b78e88-ead8-445c-9cfd-0b87f74ea6cd}', '{D6886603-9D2F-4EB2-B667-1971041FA96B}' # password, Windows Hello
$providers = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers'
$com = "HKLM:\SOFTWARE\Classes\CLSID\$clsid"
$settings = 'HKLM:\SOFTWARE\Kiosk\CredentialProvider'
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'

function Set-Excluded([string[]]$Add, [string[]]$Remove) {
    $current = (Get-ItemProperty -Path $policy -Name ExcludedCredentialProviders -ErrorAction SilentlyContinue).ExcludedCredentialProviders
    $list = @($current -split ',' | Where-Object { $_ } | ForEach-Object { $_.Trim() })
    $list = @($list | Where-Object { $item = $_; -not ($Remove | Where-Object { $_ -eq $item }) })
    foreach ($item in $Add) { if (-not ($list | Where-Object { $_ -eq $item })) { $list += $item } }
    if ($PSCmdlet.ShouldProcess("$policy\ExcludedCredentialProviders", ($list -join ','))) {
        if ($list.Count -eq 0) { Remove-ItemProperty -Path $policy -Name ExcludedCredentialProviders -ErrorAction SilentlyContinue }
        else {
            if (-not (Test-Path $policy)) { New-Item -Path $policy -Force | Out-Null }
            New-ItemProperty -Path $policy -Name ExcludedCredentialProviders -Value ($list -join ',') -PropertyType String -Force | Out-Null
        }
    }
}

if ($Uninstall) {
    Set-Excluded -Add @() -Remove (@($fido, $clsid) + $others)
    $default = (Get-ItemProperty -Path $policy -Name DefaultCredentialProvider -ErrorAction SilentlyContinue).DefaultCredentialProvider
    if ($default -eq $clsid -and $PSCmdlet.ShouldProcess("$policy\DefaultCredentialProvider", 'remove')) { Remove-ItemProperty -Path $policy -Name DefaultCredentialProvider }
    foreach ($key in "$providers\$clsid", $com, $settings) {
        if ((Test-Path $key) -and $PSCmdlet.ShouldProcess($key, 'remove')) { Remove-Item -Path $key -Recurse -Force }
    }
    Write-Output 'Kiosk sign-in tile removed; Windows tiles are back. The DLL is left in place (LogonUI may hold it).'
    return
}

if (-not (Test-Path $SourcePath)) { throw "Build the sign-in tile first (scripts\Build-CredentialProvider.ps1): $SourcePath" }
if (-not (Test-Path "$providers\$fido")) { throw "Windows security key provider $fido is not registered on this PC." }

# 1. Program file and tile picture.
$dll = Join-Path $InstallPath 'KioskCredentialProvider.dll'
if ($PSCmdlet.ShouldProcess($dll, 'copy sign-in tile')) {
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    try { Copy-Item -Path $SourcePath -Destination $dll -Force }
    catch {
        # LogonUI keeps the loaded DLL open: install the new build next to it and point the registration there.
        $dll = Join-Path $InstallPath ('KioskCredentialProvider-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.dll')
        Copy-Item -Path $SourcePath -Destination $dll -Force
    }
}
if ($PSCmdlet.ShouldProcess($TileImage, 'draw tile picture')) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force -Path (Split-Path $TileImage) | Out-Null
    $size = 448
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.Clear([System.Drawing.Color]::FromArgb(24, 35, 54))
        $accent = [System.Drawing.ColorTranslator]::FromHtml($AccentColor)
        $pen = New-Object System.Drawing.Pen $accent, 16
        $card = New-Object System.Drawing.Drawing2D.GraphicsPath
        $x = 84; $y = 124; $w = 280; $h = 200; $r = 40
        $card.AddArc($x, $y, $r, $r, 180, 90); $card.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
        $card.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $card.AddArc($x, $y + $h - $r, $r, $r, 90, 90); $card.CloseFigure()
        $graphics.DrawPath($pen, $card)
        $graphics.FillRectangle((New-Object System.Drawing.SolidBrush $accent), $x + 46, $y + 58, 74, 58)
        $bitmap.Save($TileImage, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

# 2. Registration and settings.
if ($PSCmdlet.ShouldProcess($com, 'register COM class')) {
    New-Item -Path "$com\InprocServer32" -Force | Out-Null
    Set-Item -Path $com -Value 'Kiosk sign-in tile'
    Set-Item -Path "$com\InprocServer32" -Value $dll
    New-ItemProperty -Path "$com\InprocServer32" -Name ThreadingModel -Value 'Apartment' -PropertyType String -Force | Out-Null
}
if ($PSCmdlet.ShouldProcess($settings, 'write tile settings')) {
    New-Item -Path $settings -Force | Out-Null
    New-ItemProperty -Path $settings -Name Title -Value $Title -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $settings -Name TileImage -Value $TileImage -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $settings -Name InnerProvider -Value $fido -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $settings -Name Trace -Value ([int][bool]$Trace) -PropertyType DWord -Force | Out-Null
}
if ($PSCmdlet.ShouldProcess("$providers\$clsid", 'register credential provider')) {
    New-Item -Path "$providers\$clsid" -Force | Out-Null
    Set-Item -Path "$providers\$clsid" -Value 'Kiosk'
}

# 3. Windows' own security key tile is replaced by the Kiosk one; optionally password / PIN tiles too.
$hide = @($fido) + $(if ($HideOtherOptions) { $others } else { @() })
Set-Excluded -Add $hide -Remove @()
# The Kiosk tile is the one selected first on the sign-in screen, instead of the password.
if ($PSCmdlet.ShouldProcess("$policy\DefaultCredentialProvider", $clsid)) {
    if (-not (Test-Path $policy)) { New-Item -Path $policy -Force | Out-Null }
    New-ItemProperty -Path $policy -Name DefaultCredentialProvider -Value $clsid -PropertyType String -Force | Out-Null
}

Write-Output "Done. Sign-in tile: $dll"
Write-Output 'Sign out (not Win+L) and choose Other user > Sign-in options > the Kiosk card tile.'

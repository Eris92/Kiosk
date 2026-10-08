<#
.SYNOPSIS
Gives the Windows sign-in screen the Kiosk look: Kiosk background with "Przyłóż kartę", no user list,
sign-in with a security key (FIDO2 card) turned on. Windows still does the sign-in, so the Kiosk
shell gets full SSO (Entra ID) afterwards.

.DESCRIPTION
Run as administrator. Supports -WhatIf; -Uninstall reverts everything below.

  * Background: a generated image (-ImagePath) set as lock and sign-in screen image (PersonalizationCSP).
  * Sign-in screen: no last user, no local user list, no account details, no blur over the background.
  * Security key sign-in for Entra ID accounts (same as Intune "Use security keys for sign-in").
  * -SecurityKeyOnly hides the password and Windows Hello PIN tiles for everyone, local administrators
    included. First check that a card sign-in works and that another way in exists (an Entra admin
    account with a card), otherwise nobody can sign in with a password on this PC any more.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ImagePath = (Join-Path $env:ProgramData 'Kiosk\signin.png'),
    [string]$Title = 'Przyłóż kartę',
    [string]$Hint = 'Wybierz „Klucz zabezpieczeń”, przyłóż kartę do czytnika i wpisz PIN.',
    [string]$AccentColor = '#3DDC84',
    [switch]$SecurityKeyOnly,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }

$csp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
$personalization = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization'
$systemPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
$system = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$securityKey = 'HKLM:\SOFTWARE\Microsoft\Policies\PassportForWork\SecurityKey'
# Password and Windows Hello (PIN / face / fingerprint) credential providers.
$hiddenProviders = '{60b78e88-ead8-445c-9cfd-0b87f74ea6cd},{D6886603-9D2F-4EB2-B667-1971041FA96B}'

function Set-Value($Path, $Name, $Value, $Type = 'DWord') {
    if ($PSCmdlet.ShouldProcess("$Path\$Name", "set $Value")) {
        if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    }
}

function Remove-Value($Path, $Name) {
    if ((Test-Path $Path) -and $PSCmdlet.ShouldProcess("$Path\$Name", 'remove')) {
        Remove-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue
    }
}

if ($Uninstall) {
    foreach ($name in 'LockScreenImagePath', 'LockScreenImageUrl', 'LockScreenImageStatus') { Remove-Value $csp $name }
    Remove-Value $personalization 'NoChangingLockScreen'
    foreach ($name in 'DisableAcrylicBackgroundOnLogon', 'EnumerateLocalUsers', 'BlockUserFromShowingAccountDetailsOnSignin', 'ExcludedCredentialProviders') { Remove-Value $systemPolicy $name }
    Remove-Value $securityKey 'UseSecurityKeyForSignin'
    Write-Output 'Kiosk sign-in screen removed (the image file is left in place).'
    return
}

# 1. Background image in the Kiosk colours. The sign-in tile sits in the middle, so the text goes above it.
if ($PSCmdlet.ShouldProcess($ImagePath, 'draw sign-in background')) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force -Path (Split-Path $ImagePath) | Out-Null
    $width = 3840; $height = 2160
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.TextRenderingHint = 'AntiAliasGridFit'
        $area = New-Object System.Drawing.Rectangle 0, 0, $width, $height
        $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush $area, ([System.Drawing.Color]::FromArgb(18, 30, 52)), ([System.Drawing.Color]::FromArgb(13, 20, 35)), 60
        $graphics.FillRectangle($background, $area)
        $accent = [System.Drawing.ColorTranslator]::FromHtml($AccentColor)
        $center = New-Object System.Drawing.StringFormat
        $center.Alignment = 'Center'
        # Card icon: a rounded rectangle with a chip.
        $pen = New-Object System.Drawing.Pen $accent, 12
        $card = New-Object System.Drawing.Drawing2D.GraphicsPath
        $x = $width / 2 - 150; $y = 230; $w = 300; $h = 190; $r = 36
        $card.AddArc($x, $y, $r, $r, 180, 90); $card.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
        $card.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $card.AddArc($x, $y + $h - $r, $r, $r, 90, 90); $card.CloseFigure()
        $graphics.DrawPath($pen, $card)
        $graphics.FillRectangle((New-Object System.Drawing.SolidBrush $accent), $x + 50, $y + 60, 70, 55)
        $titleFont = New-Object System.Drawing.Font 'Segoe UI Semibold', 120, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $hintFont = New-Object System.Drawing.Font 'Segoe UI', 54, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $graphics.DrawString($Title, $titleFont, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 245, 252))), (New-Object System.Drawing.RectangleF 0, 480, $width, 170), $center)
        $graphics.DrawString($Hint, $hintFont, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(157, 174, 195))), (New-Object System.Drawing.RectangleF 400, 660, ($width - 800), 160), $center)
        $bitmap.Save($ImagePath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
Set-Value $csp 'LockScreenImagePath' $ImagePath 'String'
Set-Value $csp 'LockScreenImageUrl' $ImagePath 'String'
Set-Value $csp 'LockScreenImageStatus' 1
Set-Value $personalization 'NoChangingLockScreen' 1

# 2. Sign-in screen: only "Other user", no names, the background without blur.
Set-Value $system 'dontdisplaylastusername' 1
Set-Value $systemPolicy 'EnumerateLocalUsers' 0
Set-Value $systemPolicy 'BlockUserFromShowingAccountDetailsOnSignin' 1
Set-Value $systemPolicy 'DisableAcrylicBackgroundOnLogon' 1

# 3. Security key (FIDO2 card) sign-in for Entra ID accounts.
Set-Value $securityKey 'UseSecurityKeyForSignin' 1
if ($SecurityKeyOnly) { Set-Value $systemPolicy 'ExcludedCredentialProviders' $hiddenProviders 'String' }

Write-Output "Done. Background: $ImagePath"
Write-Output 'Lock the PC (Win+L) or sign out to see the sign-in screen.'

# Dot-sourced by scripts\Install-*.ps1: Kiosk uses the .NET 10 Desktop Runtime installed in Windows (it has no .NET of its own).

# The .NET frameworks the Kiosk build in $SourcePath needs and this PC lacks; none for a self-contained build.
function Get-KioskMissingRuntime([string]$SourcePath) {
    $config = Join-Path $SourcePath 'Kiosk.Client.runtimeconfig.json'
    if (-not (Test-Path $config) -or (Test-Path (Join-Path $SourcePath 'hostfxr.dll'))) { return }
    $options = (Get-Content $config -Raw | ConvertFrom-Json).runtimeOptions
    $frameworks = @($options.frameworks) + @($options.framework) | Where-Object { $_ }
    $shared = Join-Path $env:ProgramFiles 'dotnet\shared'
    foreach ($framework in $frameworks) {
        $major = ($framework.version -split '\.')[0]
        if (-not (Get-ChildItem (Join-Path $shared $framework.name) -Directory -Filter "$major.*" -ErrorAction SilentlyContinue)) { "$($framework.name) $major" }
    }
}

# Installs the .NET 10 Desktop Runtime x64 when the build needs it: winget, else Microsoft's installer
# (only with a valid Microsoft signature). Throws when it is still missing.
function Assert-KioskRuntime([string]$SourcePath) {
    $missing = Get-KioskMissingRuntime $SourcePath
    if (-not $missing) { return }
    if ($WhatIfPreference) { Write-Output "WhatIf: zainstalowałbym .NET 10 Desktop Runtime x64 ($($missing -join ', '))."; return }
    Write-Output "Brak $($missing -join ', '): instaluję .NET 10 Desktop Runtime x64..."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        & winget install --id Microsoft.DotNet.DesktopRuntime.10 --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    }
    if (Get-KioskMissingRuntime $SourcePath) {
        # No winget (system account, some editions) or it failed: Microsoft's own installer.
        $file = Join-Path ([IO.Path]::GetTempPath()) ('windowsdesktop-runtime-win-x64-{0}.exe' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' -OutFile $file -UseBasicParsing
            $signature = Get-AuthenticodeSignature -LiteralPath $file
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Instalator .NET nie ma ważnego podpisu Microsoft.' }
            $setup = Start-Process -FilePath $file -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
            Write-Output "Instalator .NET zakończył się kodem $($setup.ExitCode)."
        } finally { Remove-Item $file -Force -ErrorAction SilentlyContinue }
    }
    if ($missing = Get-KioskMissingRuntime $SourcePath) {
        throw ("Nie udało się zainstalować $($missing -join ', '). Zainstaluj .NET 10 Desktop Runtime x64 ręcznie: " +
            'winget install Microsoft.DotNet.DesktopRuntime.10 (albo https://dotnet.microsoft.com/download/dotnet/10.0).')
    }
    Write-Output '.NET 10 Desktop Runtime zainstalowany.'
}

# After a self-contained Kiosk in $InstallPath: removes its own .NET files. With hostfxr.dll left there
# Windows would still start Kiosk as self-contained, and it would fail.
function Remove-KioskOldRuntime([string]$SourcePath, [string]$InstallPath) {
    if ((Test-Path (Join-Path $SourcePath 'hostfxr.dll')) -or -not (Test-Path (Join-Path $InstallPath 'hostfxr.dll'))) { return }
    Get-ChildItem $InstallPath -File | Where-Object { ($_.Extension -eq '.dll' -or $_.Name -eq 'createdump.exe') -and -not (Test-Path (Join-Path $SourcePath $_.Name)) } |
        Sort-Object { $_.Name -eq 'hostfxr.dll' } | Remove-Item -Force
}

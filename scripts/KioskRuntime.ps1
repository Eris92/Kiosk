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

# Installs the newest .NET 10 Desktop Runtime x64 when the build needs it: Microsoft's installer named in its
# release list, run only when its SHA-512 matches that list. No winget: it does not work in the kiosk shell.
# Throws when the runtime is still missing.
function Assert-KioskRuntime([string]$SourcePath) {
    $missing = Get-KioskMissingRuntime $SourcePath
    if (-not $missing) { return }
    if ($WhatIfPreference) { Write-Output "WhatIf: zainstalowałbym .NET 10 Desktop Runtime x64 ($($missing -join ', '))."; return }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $metadata = Invoke-RestMethod -Uri 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json' -UseBasicParsing
    $release = $metadata.releases | Where-Object { $_.'release-version' -eq $metadata.'latest-release' } | Select-Object -First 1
    $file = $release.windowsdesktop.files | Where-Object { $_.name -eq 'windowsdesktop-runtime-win-x64.exe' } | Select-Object -First 1
    if (-not $file -or -not $file.url -or -not $file.hash) { throw 'Nie znaleziono instalatora .NET 10 Desktop Runtime x64 na liście Microsoftu.' }
    $uri = [uri]$file.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'builds.dotnet.microsoft.com') { throw "Nieoczekiwany adres instalatora .NET: $($uri.Host)" }
    $version = $release.windowsdesktop.version
    $installer = Join-Path ([IO.Path]::GetTempPath()) ('windowsdesktop-runtime-{0}.exe' -f [guid]::NewGuid().ToString('N'))
    try {
        Write-Output "Brak $($missing -join ', '): pobieram .NET Desktop Runtime $version..."
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $file.url -OutFile $installer -UseBasicParsing
        if ((Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ne $file.hash) { throw 'Suma SHA-512 instalatora .NET się nie zgadza.' }
        Write-Output "Instaluję .NET Desktop Runtime $version..."
        $setup = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($setup.ExitCode -notin @(0, 3010, 1638)) { throw "Instalacja .NET nie powiodła się (kod $($setup.ExitCode))." }
        if ($setup.ExitCode -eq 3010) { Write-Warning 'Windows prosi o ponowne uruchomienie po instalacji .NET.' }
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    if ($missing = Get-KioskMissingRuntime $SourcePath) {
        throw ("Nadal brak $($missing -join ', '). Zainstaluj .NET 10 Desktop Runtime x64 ręcznie: https://dotnet.microsoft.com/download/dotnet/10.0")
    }
    Write-Output ".NET Desktop Runtime $version zainstalowany."
}

# After a self-contained Kiosk in $InstallPath: removes its own .NET files. With hostfxr.dll left there
# Windows would still start Kiosk as self-contained, and it would fail.
function Remove-KioskOldRuntime([string]$SourcePath, [string]$InstallPath) {
    if ((Test-Path (Join-Path $SourcePath 'hostfxr.dll')) -or -not (Test-Path (Join-Path $InstallPath 'hostfxr.dll'))) { return }
    Get-ChildItem $InstallPath -File | Where-Object { ($_.Extension -eq '.dll' -or $_.Name -eq 'createdump.exe') -and -not (Test-Path (Join-Path $SourcePath $_.Name)) } |
        Sort-Object { $_.Name -eq 'hostfxr.dll' } | Remove-Item -Force
}

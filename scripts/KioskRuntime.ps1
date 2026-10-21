# Dot-sourced by scripts\Install-*.ps1: Kiosk uses the .NET 10 Desktop Runtime installed in Windows (it has no .NET of its own).

# Throws when this PC lacks a .NET framework the Kiosk build in $SourcePath needs.
function Assert-KioskRuntime([string]$SourcePath) {
    $config = Join-Path $SourcePath 'Kiosk.Client.runtimeconfig.json'
    if (-not (Test-Path $config) -or (Test-Path (Join-Path $SourcePath 'hostfxr.dll'))) { return }   # Self-contained build.
    $options = (Get-Content $config -Raw | ConvertFrom-Json).runtimeOptions
    $frameworks = @($options.frameworks) + @($options.framework) | Where-Object { $_ }
    $shared = Join-Path $env:ProgramFiles 'dotnet\shared'
    $missing = foreach ($framework in $frameworks) {
        $major = ($framework.version -split '\.')[0]
        if (-not (Get-ChildItem (Join-Path $shared $framework.name) -Directory -Filter "$major.*" -ErrorAction SilentlyContinue)) { "$($framework.name) $major" }
    }
    if ($missing) {
        throw ("Kiosk wymaga $($missing -join ', '). Zainstaluj .NET 10 Desktop Runtime x64: " +
            'winget install Microsoft.DotNet.DesktopRuntime.10 (albo instalator z https://dotnet.microsoft.com/download/dotnet/10.0).')
    }
}

# After a self-contained Kiosk in $InstallPath: removes its own .NET files. With hostfxr.dll left there
# Windows would still start Kiosk as self-contained, and it would fail.
function Remove-KioskOldRuntime([string]$SourcePath, [string]$InstallPath) {
    if ((Test-Path (Join-Path $SourcePath 'hostfxr.dll')) -or -not (Test-Path (Join-Path $InstallPath 'hostfxr.dll'))) { return }
    Get-ChildItem $InstallPath -File | Where-Object { ($_.Extension -eq '.dll' -or $_.Name -eq 'createdump.exe') -and -not (Test-Path (Join-Path $SourcePath $_.Name)) } |
        Sort-Object { $_.Name -eq 'hostfxr.dll' } | Remove-Item -Force
}

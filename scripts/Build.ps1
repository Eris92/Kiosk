#Requires -Version 5.1
<#
.SYNOPSIS
Publishes a clean, self-contained Windows client. Local settings are never included implicitly.
.DESCRIPTION
Use -ConfigPath explicitly for a configured local build. For distributable artifacts use New-Package.ps1.
The publish is staged beside OutputPath; a failed publish leaves the previous build intact.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$ConfigPath = '',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot '..\out\client' }
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot '..\config\client.json' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\')
if ($SelfTest -and -not $PSBoundParameters.ContainsKey('OutputPath')) { $destination = Join-Path $repository 'out\self-test' }
foreach ($protectedRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:SystemRoot) | Where-Object { $_ }) {
    $protected = [IO.Path]::GetFullPath($protectedRoot).TrimEnd('\')
    if ($destination -eq $protected -or $destination.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build into a dedicated workspace directory; use deployment scripts for Program Files.' }
}
if ($destination -eq [IO.Path]::GetPathRoot($destination).TrimEnd('\') -or
    $destination -eq $repository -or $repository.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($repository + '\src', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($repository + '\config', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($repository + '\scripts', [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($repository + '\.git', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be a dedicated build directory outside the source/config/scripts directories.'
}
$configSource = (Resolve-Path -LiteralPath $ConfigPath).ProviderPath
Get-Content -LiteralPath $configSource -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null
$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$staging = Join-Path $parent ('.kiosk-publish-' + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ('.kiosk-previous-' + [Guid]::NewGuid().ToString('N'))
$published = $false
try {
    $arguments = @('publish', (Join-Path $repository 'src\Kiosk.Client\Kiosk.Client.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $staging, '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true')
    if ($SelfTest) { $arguments += '-p:KioskSelfTest=true' }
    else { $arguments += @('-p:DebugType=None', '-p:DebugSymbols=false') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed; the previous build was retained.' }
    $configTool = Join-Path $repository 'src\Kiosk.ConfigTool\KioskKonfiguracje.csproj'
    & dotnet publish $configTool -c Release -r win-x64 --self-contained false -o $staging --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building the configuration manager failed; the previous build was retained.' }
    $newsEditor = Join-Path $repository 'src\Kiosk.NewsEditor\KioskKomunikaty.csproj'
    & dotnet publish $newsEditor -c Release -r win-x64 --self-contained false -o (Join-Path $staging 'news-example') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building the news editor failed; the previous build was retained.' }
    $fido = Join-Path $repository 'src\Kiosk.FidoBroker\FidoRun.csproj'
    & dotnet build $fido -c Release -o (Join-Path $staging 'fido') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building the card sign-in broker failed; the previous build was retained.' }
    Get-ChildItem -LiteralPath (Join-Path $staging 'fido') -Filter '*.pdb' -File | Remove-Item -Force
    Copy-Item -LiteralPath $configSource -Destination (Join-Path $staging 'client.json')
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'Kiosk.Client.exe') -PathType Leaf)) { throw 'Publish did not produce Kiosk.Client.exe.' }
    Assert-KioskClientConfig (Join-Path $staging 'Kiosk.Client.exe') (Join-Path $staging 'client.json')
    if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination $previous }
    try { Move-Item -LiteralPath $staging -Destination $destination; $published = $true }
    catch {
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $destination }
        throw
    }
}
finally {
    # Only generated paths, directly under the verified destination's parent, may be removed.
    foreach ($temporary in @($staging) + $(if ($published) { @($previous) } else { @() })) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        if ((Split-Path -Parent $resolved) -ne $parent -or (Split-Path -Leaf $resolved) -notmatch '^\.kiosk-(publish|previous)-[a-f0-9]{32}$') { throw 'Unexpected build cleanup path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
Write-Output "Build ready: $destination"
Write-Output 'Configuration came only from ConfigPath or the public default; client.local.json was not auto-discovered.'

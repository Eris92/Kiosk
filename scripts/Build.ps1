#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\out\client'))
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 8 SDK or newer first.' }
$project = Join-Path $PSScriptRoot '..\src\Kiosk.Client\Kiosk.Client.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$localConfig = Join-Path $PSScriptRoot '..\client.local.json'
$outputConfig = Join-Path $OutputPath 'client.json'
if (Test-Path -LiteralPath $localConfig) {
    Copy-Item -LiteralPath $localConfig -Destination $outputConfig -Force
} elseif (-not (Test-Path -LiteralPath $outputConfig)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\config\client.json') -Destination $outputConfig
}
Write-Output "Build ready: $OutputPath"

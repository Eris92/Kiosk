#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\out\client'))
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 8 SDK or newer first.' }
$project = Join-Path $PSScriptRoot '..\src\Kiosk.Client\Kiosk.Client.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item (Join-Path $PSScriptRoot '..\config\client.json') (Join-Path $OutputPath 'client.json') -Force
Write-Output "Build ready: $OutputPath"

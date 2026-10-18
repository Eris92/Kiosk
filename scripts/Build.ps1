#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\out\client'),
    # Release tag (build-N / vN) shown in Konfiguracja → Aktualizacja; empty = local "dev" build.
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 8 SDK or newer first.' }
$project = Join-Path $PSScriptRoot '..\src\Kiosk.Client\Kiosk.Client.csproj'
$versionArguments = if ($Version) { @("-p:InformationalVersion=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false') } else { @() }
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $OutputPath @versionArguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
# The card sign-in program (card + PIN signs the person in to Windows), .NET Framework 4.8: out\client\fido.
$fido = Join-Path $PSScriptRoot '..\src\Kiosk.FidoBroker\FidoRun.csproj'
& dotnet build $fido -c Release -o (Join-Path $OutputPath 'fido') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Building the card sign-in program failed.' }
Remove-Item (Join-Path $OutputPath 'fido\*.pdb') -ErrorAction SilentlyContinue
$localConfig = Join-Path $PSScriptRoot '..\client.local.json'
$outputConfig = Join-Path $OutputPath 'client.json'
if (Test-Path -LiteralPath $localConfig) {
    Copy-Item -LiteralPath $localConfig -Destination $outputConfig -Force
} elseif (-not (Test-Path -LiteralPath $outputConfig)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\config\client.json') -Destination $outputConfig
}
Write-Output "Build ready: $OutputPath"

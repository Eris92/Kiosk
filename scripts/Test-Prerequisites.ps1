#Requires -Version 5.1
[CmdletBinding()]
param([string]$ConfigPath = (Join-Path $PSScriptRoot '..\config\client.json'))
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$service = Get-Service SCardSvr
# Connections list, or the legacy single Server/Port fields.
$connections = @(if ($config.Connections) { $config.Connections } else { [PSCustomObject]@{ Name = $config.Server; Server = $config.Server; Port = $config.Port } })
[PSCustomObject]@{
    OS = (Get-CimInstance Win32_OperatingSystem).Caption
    Process64Bit = [Environment]::Is64BitProcess
    SmartCardService = [string]$service.Status
    RdpActiveX = Test-Path 'Registry::HKEY_CLASSES_ROOT\CLSID\{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}'
    TestMode = $config.TestMode
} | Format-List
$connections | ForEach-Object {
    $port = if ($_.Port) { [int]$_.Port } else { 3389 }
    [PSCustomObject]@{
        Name = if ($_.Name) { $_.Name } else { $_.Server }
        Server = $_.Server
        Port = $port
        RdpPortReachable = Test-NetConnection -ComputerName $_.Server -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
    }
} | Format-Table -AutoSize
Write-Output 'For reader/certificate/PIN diagnostics run: certutil.exe -scinfo'

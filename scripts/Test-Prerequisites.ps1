#Requires -Version 5.1
[CmdletBinding()]
param([string]$ConfigPath = (Join-Path $PSScriptRoot '..\config\client.json'))
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$service = Get-Service SCardSvr
$port = Test-NetConnection -ComputerName $config.Server -Port $config.Port -InformationLevel Quiet -WarningAction SilentlyContinue
[PSCustomObject]@{
    OS = (Get-CimInstance Win32_OperatingSystem).Caption
    Process64Bit = [Environment]::Is64BitProcess
    SmartCardService = [string]$service.Status
    RdpActiveX = Test-Path 'Registry::HKEY_CLASSES_ROOT\CLSID\{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}'
    Server = $config.Server
    Port = $config.Port
    RdpPortReachable = $port
    TestMode = $config.TestMode
    RequirePin = $config.RequirePin
} | Format-List
Write-Output 'For reader/certificate/PIN diagnostics run: certutil.exe -scinfo'

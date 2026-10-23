#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot '..\config\client.json'),
    [switch]$CheckNetwork,
    [ValidateRange(1,30)][int]$TimeoutSeconds = 3
)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$readers = Get-Service SCardSvr -ErrorAction SilentlyContinue
$connections = @($config.RdpConnections)
if ($connections.Count -eq 0 -and $config.Server) { $connections = @([pscustomobject]@{Name='Legacy RDP';Server=$config.Server;Port=$config.Port}) }
[PSCustomObject]@{
    OS = (Get-CimInstance Win32_OperatingSystem).Caption
    Process64Bit = [Environment]::Is64BitProcess
    SmartCardService = if ($readers) { [string]$readers.Status } else { 'Not installed' }
    KioskCardService = [string](Get-Service KioskCard -ErrorAction SilentlyContinue).Status
    RdpActiveX = Test-Path 'Registry::HKEY_CLASSES_ROOT\CLSID\{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}'
    SessionMode = $config.SessionMode
    RdpProfiles = @($connections | Where-Object { $_ }).Count
    TestMode = $config.TestMode
    ConfigPath = (Resolve-Path -LiteralPath $ConfigPath).ProviderPath
} | Format-List
if ($CheckNetwork) {
    foreach ($connection in $connections | Where-Object { $_ }) {
        $client = New-Object Net.Sockets.TcpClient
        $reachable = $false
        try {
            $port = if ($connection.Port) { [int]$connection.Port } else { 3389 }
            $pending = $client.BeginConnect([string]$connection.Server, $port, $null, $null)
            if ($pending.AsyncWaitHandle.WaitOne($TimeoutSeconds * 1000)) { $client.EndConnect($pending); $reachable = $true }
        } catch { $reachable = $false }
        finally { $client.Dispose() }
        [pscustomobject]@{Name=$connection.Name;Server=$connection.Server;Port=$port;TcpReachable=$reachable}
    }
}
Write-Output 'Check Microsoft Edge WebView2 Runtime for the embedded browser. certutil.exe -scinfo performs interactive certificate/PIN diagnostics.'

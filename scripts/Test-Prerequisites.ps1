#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ConfigPath = '',
    [switch]$CheckNetwork,
    [ValidateRange(1,30)][int]$TimeoutSeconds = 3
)
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot '..\config\client.json' }
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$readers = Get-Service SCardSvr -ErrorAction SilentlyContinue
$cardService = Get-Service KioskCard -ErrorAction SilentlyContinue
$connections = @($config.RdpConnections)
if ($connections.Count -eq 0 -and $config.Server) { $connections = @([pscustomobject]@{Name='Legacy RDP';Server=$config.Server;Port=$config.Port}) }
$joinFlags = @{ AzureAdJoined = 'Unknown'; DomainJoined = 'Unknown'; NgcSet = 'Unknown'; AzureAdPrt = 'Unknown' }
$dsreg = Get-Command dsregcmd.exe -ErrorAction SilentlyContinue
if ($dsreg) {
    try {
        # Run in the signed-in user's context: NgcSet and AzureAdPrt are user-specific.
        $statusText = (& $dsreg.Source /status 2>$null) -join "`n"
        foreach ($key in @($joinFlags.Keys)) {
            if ($statusText -match "(?m)^\s*$key\s*:\s*(YES|NO)\s*$") { $joinFlags[$key] = $Matches[1].ToUpperInvariant() }
        }
    } catch {
        # Older/unavailable dsregcmd versions must not hide the other diagnostic results.
    }
}
$deviceJoin = if ($joinFlags.AzureAdJoined -eq 'YES' -and $joinFlags.DomainJoined -eq 'YES') { 'Hybrid AD + Entra' }
    elseif ($joinFlags.AzureAdJoined -eq 'YES' -and $joinFlags.DomainJoined -eq 'NO') { 'Entra joined' }
    elseif ($joinFlags.AzureAdJoined -eq 'NO' -and $joinFlags.DomainJoined -eq 'YES') { 'AD domain joined' }
    elseif ($joinFlags.AzureAdJoined -eq 'NO' -and $joinFlags.DomainJoined -eq 'NO') { 'Workgroup / local device' }
    else { 'Unknown' }
[PSCustomObject]@{
    OS = (Get-CimInstance Win32_OperatingSystem).Caption
    Process64Bit = [Environment]::Is64BitProcess
    WindowsAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    DeviceJoin = $deviceJoin
    HelloKeyForCurrentUser = $joinFlags.NgcSet
    EntraPrtForCurrentUser = $joinFlags.AzureAdPrt
    SmartCardService = if ($readers) { [string]$readers.Status } else { 'Not installed' }
    KioskCardService = if ($cardService) { [string]$cardService.Status } else { 'Not installed' }
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
Write-Output 'Device join and Hello/PRT indicators describe this device/current Windows user only; they do not prove card ownership or RDP sign-in. Check WebView2 Runtime separately. certutil.exe -scinfo performs interactive certificate/PIN diagnostics.'

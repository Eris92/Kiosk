#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$BrokerPath)
$ErrorActionPreference = 'Stop'

$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($BrokerPath))
$type = $assembly.GetType('FidoRun.BrokerPolicy', $true)
$flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$pinClassifier = $assembly.GetType('FidoRun.HostForm', $true).GetMethod('IsRejectedPin', $flags)
foreach ($message in @('Kod PIN jest niepoprawny. Spróbuj ponownie.', 'Nieprawidłowy PIN', 'Błędny PIN', 'The PIN is incorrect.', 'Wrong PIN')) {
    if (-not $pinClassifier.Invoke($null, @($message))) { throw 'A rejected PIN notification was not recognized.' }
}
foreach ($message in @('Wpisz PIN karty.', 'Sprawdzanie PIN-u…', 'Przyłóż kartę do czytnika.')) {
    if ($pinClassifier.Invoke($null, @($message))) { throw 'A normal authentication instruction was classified as a rejected PIN.' }
}
$configure = $type.GetMethod('Configure', $flags)
$check = $type.GetMethod('Check', $flags)
if ($null -eq $configure -or $null -eq $check) { throw 'Broker policy methods are missing.' }
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
public static class KioskBrokerPolicyTest
{
    public static void Log(string message) { }
    public static void Configure(MethodInfo method, string file, Action<string> logger) { method.Invoke(null, new object[] { file, logger }); }
    public static string Check(MethodInfo method, string program, string arguments) { return (string)method.Invoke(null, new object[] { program, arguments }); }
}
'@

$allowlist = Join-Path ([IO.Path]::GetTempPath()) ('kiosk-broker-policy-' + [guid]::NewGuid().ToString('N') + '.txt')
$program = Join-Path $env:WINDIR 'System32\notepad.exe'
$other = Join-Path $env:WINDIR 'System32\mspaint.exe'
$commandProgram = Join-Path $env:WINDIR 'System32\cmd.exe'
$command = '"' + $commandProgram + '" /c echo ready'
[IO.File]::WriteAllLines($allowlist, [string[]]@(('P ' + $program), ('C ' + $commandProgram + "`t" + $command)), (New-Object Text.UTF8Encoding($false)))
try {
    $logger = [Delegate]::CreateDelegate([Action[string]], [KioskBrokerPolicyTest].GetMethod('Log'))
    [KioskBrokerPolicyTest]::Configure($configure, $allowlist, $logger)
    if ([IO.File]::Exists($allowlist)) { throw 'Broker did not remove the consumed allowlist.' }
    $allowedResult = [KioskBrokerPolicyTest]::Check($check, $program, 'any arguments')
    if ($null -ne $allowedResult) { throw "Allowed program was rejected: $allowedResult" }
    if ($null -ne [KioskBrokerPolicyTest]::Check($check, $commandProgram, $command)) { throw 'Allowed command was rejected.' }
    if ($null -eq [KioskBrokerPolicyTest]::Check($check, $other, $command)) { throw 'Allowed command text was accepted for a different executable.' }
    if ($null -eq [KioskBrokerPolicyTest]::Check($check, $other, 'other arguments')) { throw 'Unlisted program was accepted.' }
    # The policy is now held in memory. A later file at the same path cannot change this broker's rules.
    [IO.File]::WriteAllText($allowlist, 'P ' + $other)
    if ($null -ne [KioskBrokerPolicyTest]::Check($check, $program, 'any arguments') -or
        $null -eq [KioskBrokerPolicyTest]::Check($check, $other, 'other arguments')) {
        throw 'Broker policy changed after configuration.'
    }
    Write-Host 'Broker allowlist snapshot, cleanup and program/command checks passed.'
} finally {
    if ([IO.File]::Exists($allowlist)) { [IO.File]::Delete($allowlist) }
}

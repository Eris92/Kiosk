#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | ForEach-Object {
    $errors = $null; $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
}
Get-ChildItem -LiteralPath (Join-Path $root 'config') -Filter '*.json' | ForEach-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    $text | ConvertFrom-Json | Out-Null
    if ($text -match '(?i)C:\\\\Users\\\\(?!Public\\\\)|X:\\\\|"(?:Password|Pin|Secret|AccessToken)"\s*:') { throw "Private workstation data or a credential field was found in public configuration: $($_.Name)" }
}
[xml](Get-Content -LiteralPath (Join-Path $root 'config\intune\ShellLauncher.xml') -Raw) | Out-Null
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
$validDataFile = Join-Path $env:ProgramData 'Kiosk\client.json'
if ((Assert-KioskDataFilePath $validDataFile '.json') -ne [IO.Path]::GetFullPath($validDataFile)) {
    throw 'Deployment rejected its expected data file.'
}
foreach ($unsafe in @(
    (Join-Path $env:ProgramData 'Kiosk\nested\client.json'),
    (Join-Path $env:ProgramData 'client.json'),
    (Join-Path $env:ProgramData 'Kiosk\client.ps1')
)) {
    try { Assert-KioskDataFilePath $unsafe '.json' | Out-Null }
    catch { continue }
    throw "Deployment accepted an unsafe data path: $unsafe"
}
$testId = [Guid]::NewGuid().ToString('N')
$key = 'HKCU:\Software\Kiosk.DeploymentTests\' + $testId
$file = Join-Path ([IO.Path]::GetTempPath()) ('kiosk-deployment-test-' + $testId + '.clixml')
$journal = @{ File = $file; State = @{ Version = 1; Values = @() } }
try {
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name Existing -Value 7 -PropertyType DWord | Out-Null
    Set-KioskRegistryValue $journal $key 'Existing' 11 'DWord'
    Set-KioskRegistryValue $journal $key 'Existing' 12 'DWord'
    Set-KioskRegistryValue $journal $key 'NewValue' 'created' 'String'
    Set-KioskRegistryValue $journal $key '' 'default value' 'String'
    New-ItemProperty -LiteralPath $key -Name Excluded -Value 'A' -PropertyType String | Out-Null
    Set-KioskRegistryValue $journal $key 'Excluded' 'A,B' 'String' -CommaSet
    Set-ItemProperty -LiteralPath $key -Name Excluded -Value 'A,B,C'
    Set-KioskRegistryValue $journal $key 'LaterPolicy' 'kiosk' 'String'
    Set-ItemProperty -LiteralPath $key -Name LaterPolicy -Value 'device-management'
    # Exercise deserialization too: installation and uninstall are separate processes.
    $journal.State = Import-Clixml -LiteralPath $file
    Restore-KioskRegistryJournal $journal 3>$null
    $values = Get-ItemProperty -LiteralPath $key
    if ($values.Existing -ne 7 -or (Get-KioskRegistryValue $key 'NewValue').Exists -or (Get-KioskRegistryValue $key '').Exists) { throw 'Registry rollback did not retain original/absent values.' }
    if ($values.Excluded -ne 'A,C') { throw 'Provider rollback removed a later policy entry.' }
    if ($values.LaterPolicy -ne 'device-management' -or $journal.State.Values.Count -ne 1) { throw 'Registry rollback did not retain a later administrator change.' }
} finally {
    if ($key -notmatch '^HKCU:\\Software\\Kiosk\.DeploymentTests\\[a-f0-9]{32}$') { throw 'Unexpected registry test cleanup path.' }
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
}
Write-Output 'PowerShell/JSON/XML validation and isolated HKCU rollback tests passed.'

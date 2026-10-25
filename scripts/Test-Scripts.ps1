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
$configFixtureRoot = [IO.Path]::GetFullPath((Join-Path $root ('out\config-source-' + [Guid]::NewGuid().ToString('N'))))
try {
    New-Item -ItemType Directory -Path $configFixtureRoot -Force | Out-Null
    $installedConfig = Join-Path $configFixtureRoot 'installed.json'
    $newConfig = Join-Path $configFixtureRoot 'new.json'
    [IO.File]::WriteAllText($newConfig, '{"SelfServiceAuthentication":"WindowsSession"}')
    if ((Get-KioskInstallConfigSource $installedConfig $newConfig $false) -ne $newConfig) {
        throw 'A fresh installation did not select the source configuration.'
    }
    [IO.File]::WriteAllText($installedConfig, '{"SelfServiceAuthentication":"CardBroker"}')
    if ((Get-KioskInstallConfigSource $installedConfig $newConfig $false) -ne $installedConfig) {
        throw 'An ordinary upgrade replaced the installed configuration.'
    }
    if ((Get-KioskInstallConfigSource $installedConfig $newConfig $true) -ne $newConfig) {
        throw 'An explicit source did not override the installed configuration.'
    }
    try {
        Get-KioskInstallConfigSource $installedConfig (Join-Path $configFixtureRoot 'missing.json') $true | Out-Null
        throw 'A missing explicit source was accepted.'
    } catch {
        if ($_.Exception.Message -notmatch '^Kiosk configuration source does not exist:') { throw }
    }
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($configFixtureRoot)
    $outputRoot = [IO.Path]::GetFullPath((Join-Path $root 'out')).TrimEnd('\')
    if ((Split-Path -Parent $resolvedFixture) -ne $outputRoot -or
        (Split-Path -Leaf $resolvedFixture) -notmatch '^config-source-[a-f0-9]{32}$') { throw 'Unexpected configuration test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
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
$outRoot = [IO.Path]::GetFullPath((Join-Path $root 'out')).TrimEnd('\')
$atomicRoot = Join-Path $outRoot ('atomic-config-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $atomicRoot -Force | Out-Null
    $atomicFile = Join-Path $atomicRoot 'client.json'
    [IO.File]::WriteAllText($atomicFile, '{"value":1}')
    $protectedAcl = Get-Acl -LiteralPath $atomicFile
    $protectedAcl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $atomicFile -AclObject $protectedAcl
    $originalAcl = (Get-Acl -LiteralPath $atomicFile).Sddl
    Set-KioskConfigAtomically $atomicFile '{"value":2}'
    if ([IO.File]::ReadAllText($atomicFile) -ne '{"value":2}' -or (Get-Acl -LiteralPath $atomicFile).Sddl -ne $originalAcl) {
        throw 'Atomic configuration replacement changed content or ACL unexpectedly.'
    }
    if (@(Get-ChildItem -LiteralPath $atomicRoot -Force).Count -ne 1) { throw 'Atomic configuration replacement left a temporary file.' }
    $fresh = Join-Path $atomicRoot 'fresh.json'
    Set-KioskConfigAtomically $fresh '{"value":3}'
    if ([IO.File]::ReadAllText($fresh) -ne '{"value":3}') { throw 'Atomic configuration creation failed.' }
} finally {
    $resolvedAtomic = [IO.Path]::GetFullPath($atomicRoot)
    if (-not $resolvedAtomic.StartsWith($outRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedAtomic) -notmatch '^atomic-config-[a-f0-9]{32}$') { throw 'Unexpected atomic config test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedAtomic) { Remove-Item -LiteralPath $resolvedAtomic -Recurse -Force }
}
$payloadRoot = Join-Path $outRoot ('copy-payload-' + [Guid]::NewGuid().ToString('N'))
try {
    $payloadSource = Join-Path $payloadRoot 'source'
    $payloadTarget = Join-Path $payloadRoot 'target'
    New-Item -ItemType Directory -Path (Join-Path $payloadSource 'lib'), $payloadTarget -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $payloadSource 'Kiosk.Client.exe'), 'exe')
    [IO.File]::WriteAllText((Join-Path $payloadSource 'lib\support.dll'), 'library')
    [IO.File]::WriteAllText((Join-Path $payloadSource 'client.json'), 'local config excluded')
    [IO.File]::WriteAllText((Join-Path $payloadSource 'self-test.txt'), 'test output excluded')
    Copy-KioskPayloadVerified $payloadSource $payloadTarget
    if (@(Get-KioskPayloadFiles $payloadTarget).Count -ne 2 -or
        [IO.File]::ReadAllText((Join-Path $payloadTarget 'lib\support.dll')) -ne 'library') {
        throw 'Verified payload copy omitted or included an unexpected file.'
    }
    [IO.File]::WriteAllText((Join-Path $payloadTarget 'rogue.txt'), 'unlisted')
    $rejected = $false
    try { Copy-KioskPayloadVerified $payloadSource $payloadTarget }
    catch {
        if ($_.Exception.Message -ne 'Client payload copy has missing or unexpected files.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Payload verification accepted an unexpected destination file.' }
} finally {
    $resolvedPayload = [IO.Path]::GetFullPath($payloadRoot)
    if (-not $resolvedPayload.StartsWith($outRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedPayload) -notmatch '^copy-payload-[a-f0-9]{32}$') { throw 'Unexpected payload test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedPayload) { Remove-Item -LiteralPath $resolvedPayload -Recurse -Force }
}
$fixture = Join-Path $outRoot ('verify-fixture-' + [Guid]::NewGuid().ToString('N'))
$verify = Join-Path $PSScriptRoot 'Verify-Package.ps1'
try {
    $required = @(
        'client\Kiosk.Client.exe', 'client\Kiosk.Client.dll', 'client\KioskKonfiguracje.exe',
        'client\news-example\KioskKomunikaty.exe', 'client\news-example\KioskKomunikaty.dll',
        'client\fido\FidoRun.exe', 'client\client.json',
        'credential-provider\KioskCredentialProvider.dll', 'config\client.json',
        'scripts\Install-KioskShell.ps1', 'scripts\Install-KioskCardService.ps1',
        'scripts\Install-KioskConfig.ps1',
        'scripts\Install-KioskCredentialProvider.ps1', 'scripts\Set-KioskSignInScreen.ps1'
    )
    foreach ($name in $required) {
        $target = Join-Path $fixture $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        if ($name -in @('client\client.json','config\client.json')) {
            Copy-Item -LiteralPath (Join-Path $root 'config\client.json') -Destination $target
        } else { [IO.File]::WriteAllText($target, 'fixture') }
    }
    $entries = @(Get-ChildItem -LiteralPath $fixture -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{Path=$_.FullName.Substring($fixture.Length + 1).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $manifest = [ordered]@{Format=1;Files=$entries}
    $manifestPath = Join-Path $fixture 'package.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    & $verify -PackagePath $fixture | Out-Null
    try { & $verify -PackagePath $fixture -RequireSigned | Out-Null; throw 'Verifier accepted unsigned client binaries.' }
    catch { if ($_.Exception.Message -notmatch 'trusted Authenticode signature') { throw } }

    $hidden = Join-Path $fixture 'client\unlisted.txt'
    [IO.File]::WriteAllText($hidden, 'must be rejected')
    [IO.File]::SetAttributes($hidden, [IO.FileAttributes]::Hidden)
    try { & $verify -PackagePath $fixture | Out-Null; throw 'Verifier accepted a hidden unlisted file.' }
    catch { if ($_.Exception.Message -notmatch 'unlisted, duplicated or missing') { throw } }
    Remove-Item -LiteralPath $hidden -Force

    $rogue = Join-Path $fixture 'client\Rogue.exe'
    [IO.File]::WriteAllText($rogue, 'must be rejected')
    $manifest.Files += [ordered]@{Path='client/Rogue.exe';Sha256=(Get-FileHash -LiteralPath $rogue -Algorithm SHA256).Hash.ToLowerInvariant()}
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    try { & $verify -PackagePath $fixture | Out-Null; throw 'Verifier accepted an unexpected executable.' }
    catch { if ($_.Exception.Message -notmatch 'Unexpected executable') { throw } }
    $manifest.Files = @($manifest.Files | Where-Object { $_.Path -ne 'client/Rogue.exe' })
    Remove-Item -LiteralPath $rogue -Force

    $manifest.Files[0].Path = 'client/../outside.bin'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    try { & $verify -PackagePath $fixture | Out-Null; throw 'Verifier accepted a traversal path.' }
    catch { if ($_.Exception.Message -notmatch 'Invalid path in package manifest') { throw } }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path -Parent $resolved) -ne $outRoot -or (Split-Path -Leaf $resolved) -notmatch '^verify-fixture-[a-f0-9]{32}$') {
        throw 'Unexpected package test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Output 'PowerShell/JSON/XML, HKCU rollback and isolated package verifier tests passed.'

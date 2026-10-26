#Requires -Version 5.1
# Shared deployment helpers. Dot-sourcing this file changes no machine settings.
function Assert-KioskAdministrator {
    if (-not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell.' }
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
}

function Assert-KioskCardServiceReady {
    # Two requests also verify that the single-instance pipe is reopened after a client disconnects.
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'KioskCard', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(5000)
            $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
            $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
            try {
                $writer.AutoFlush = $true
                # An unknown operation checks the listener without asking for a card, PIN or credential.
                $writer.WriteLine('{"Op":"health","Reader":"probe"}')
                $read = $reader.ReadLineAsync()
                if (-not $read.Wait([TimeSpan]::FromSeconds(5))) { throw 'The card service did not answer the readiness probe.' }
                $response = $read.Result | ConvertFrom-Json
                if ($null -eq $response -or $response.Code -ne -2) { throw 'The card service returned an unexpected readiness response.' }
            } finally {
                $reader.Dispose()
                $writer.Dispose()
            }
        } finally {
            $pipe.Dispose()
        }
    }
}

function Assert-KioskClientConfig([string]$Executable, [string]$ConfigPath) {
    $report = Join-Path ([IO.Path]::GetTempPath()) ('kiosk-validation-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $arguments = '--validate-config "{0}" "{1}"' -f $ConfigPath, $report
    # Validation only writes a temporary report; it must still run during an installer's -WhatIf preflight.
    # Windows PowerShell 5.1 Start-Process has no -WhatIf parameter, so set the preference in this scope.
    $previousWhatIf = $WhatIfPreference
    try {
        $WhatIfPreference = $false
        $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    } finally { $WhatIfPreference = $previousWhatIf }
    try {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit(5000) | Out-Null
            throw 'Client configuration validation timed out.'
        }
        $detail = if (Test-Path -LiteralPath $report) { [IO.File]::ReadAllText($report) } else { 'The client did not return a validation report.' }
        if ($process.ExitCode -ne 0 -or $detail -ne 'OK') { throw "Client configuration validation failed: $detail" }
    } finally {
        $process.Dispose()
        if (Test-Path -LiteralPath $report) { [IO.File]::Delete($report) }
    }
}

function Get-KioskClientSource {
    $root = Split-Path -Parent $PSScriptRoot
    if (Test-Path -LiteralPath (Join-Path $root 'src\Kiosk.Client\Kiosk.Client.csproj')) { return Join-Path $root 'out\client' }
    return Join-Path $root 'client'
}

function Assert-KioskPackagedSource([string]$Source, [string]$Role, [bool]$RequireSigned, [string]$TrustedSignerThumbprint) {
    if ($Role -notin @('client', 'credential-provider')) { throw 'Unsupported Kiosk package source role.' }
    $full = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $packageRoot = [IO.Path]::GetDirectoryName($full)
    $manifest = Join-Path $packageRoot 'package.json'
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        $expected = [IO.Path]::GetFullPath((Join-Path $packageRoot $Role)).TrimEnd('\')
        if (-not [string]::Equals($full, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'SourcePath does not point to the expected Kiosk package directory.'
        }
        $verified = & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $packageRoot -RequireSigned:$RequireSigned -TrustedSignerThumbprint $TrustedSignerThumbprint -PassThru
        $hashes = @{}
        $prefix = $Role + '/'
        foreach ($entry in @($verified.Files)) {
            if (-not $entry.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $entry.Path.Substring($prefix.Length)
            if ($Role -eq 'client' -and ($relative -eq 'client.json' -or $relative -like 'self-test*')) { continue }
            $hashes[$relative] = [string]$entry.Sha256
        }
        return $hashes
    } elseif ($RequireSigned -or $TrustedSignerThumbprint) {
        throw 'A signed deployment requires SourcePath from a sealed Kiosk package.'
    }
}

function Get-KioskInstallConfigSource([string]$DestinationPath, [string]$SourcePath, [bool]$ExplicitSource) {
    $selected = if ($ExplicitSource -or -not (Test-Path -LiteralPath $DestinationPath -PathType Leaf)) {
        $SourcePath
    } else {
        $DestinationPath
    }
    if (-not (Test-Path -LiteralPath $selected -PathType Leaf)) { throw "Kiosk configuration source does not exist: $selected" }
    return (Resolve-Path -LiteralPath $selected).ProviderPath
}

function Get-KioskPayloadFiles([string]$Root, [switch]$SkipLocalFiles) {
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "Client payload directory does not exist: $full" }
    if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Client payload directory is a link: $full" }
    $prefix = $full + '\'
    $files = New-Object 'System.Collections.Generic.List[object]'
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($full)
    while ($pending.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $relative = $entry.FullName.Substring($prefix.Length)
            $top = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
            if ($SkipLocalFiles -and ($top -eq 'client.json' -or $top -like 'self-test*')) { continue }
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Client payload contains a link: $($entry.FullName)" }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            else { $files.Add([pscustomobject]@{ Relative = $relative.Replace('\','/'); FullName = $entry.FullName }) }
        }
    }
    return $files.ToArray()
}

function Copy-KioskPayloadVerified([string]$Source, [string]$Target, [hashtable]$ExpectedHashes = $null) {
    $expected = @(Get-KioskPayloadFiles $Source -SkipLocalFiles)
    if ($expected.Count -eq 0) { throw 'Client payload has no files.' }
    if ($null -ne $ExpectedHashes -and $ExpectedHashes.Count -ne $expected.Count) {
        throw 'Client payload differs from the verified package inventory.'
    }
    $hashes = @{}
    foreach ($file in $expected) {
        if ($hashes.ContainsKey($file.Relative)) { throw "Duplicate client payload path: $($file.Relative)" }
        $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ($null -ne $ExpectedHashes -and
            (-not $ExpectedHashes.ContainsKey($file.Relative) -or $ExpectedHashes[$file.Relative] -ne $sourceHash)) {
            throw "Client payload changed since package verification: $($file.Relative)"
        }
        $hashes[$file.Relative] = $sourceHash
    }
    Get-ChildItem -LiteralPath $Source -Force | Where-Object { $_.Name -ne 'client.json' -and $_.Name -notlike 'self-test*' } |
        Copy-Item -Destination $Target -Recurse -Force
    $actual = @(Get-KioskPayloadFiles $Target)
    if ($actual.Count -ne $expected.Count) { throw 'Client payload copy has missing or unexpected files.' }
    $seen = @{}
    foreach ($file in $actual) {
        if ($seen.ContainsKey($file.Relative) -or -not $hashes.ContainsKey($file.Relative) -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $hashes[$file.Relative]) {
            throw "Client payload copy verification failed: $($file.Relative)"
        }
        $seen[$file.Relative] = $true
    }
}

function Set-KioskDirectoryAcl([string]$Path, [switch]$UsersRead) {
    $ancestor = [IO.Path]::GetFullPath($Path)
    if ($ancestor.TrimEnd('\') -eq [IO.Path]::GetPathRoot($ancestor).TrimEnd('\')) { throw 'A filesystem root cannot be a Kiosk managed directory.' }
    foreach ($protectedRoot in @($env:ProgramData, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:SystemRoot, $env:USERPROFILE) | Where-Object { $_ }) {
        if ($ancestor.TrimEnd('\') -eq [IO.Path]::GetFullPath($protectedRoot).TrimEnd('\')) { throw 'Use a dedicated Kiosk directory, not a shared system/profile directory.' }
    }
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Managed directory contains a junction or symbolic link: $ancestor" }
        $ancestor = Split-Path -Parent $ancestor
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $rights = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    if ($UsersRead) { $rights += '*S-1-5-32-545:(OI)(CI)RX' }
    & icacls.exe $Path /reset | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot reset directory ACL: $Path" }
    & icacls.exe $Path /inheritance:r /grant:r @rights | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot protect directory: $Path" }
}

function Assert-KioskProgramPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $programs = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
    if (-not $full.StartsWith($programs + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'InstallPath must be a dedicated directory below 64-bit Program Files.' }
    $ancestor = $full
    while ($ancestor.Length -ge $programs.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Installation path contains a junction or symbolic link: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $full
}

function Assert-KioskDataFilePath([string]$Path, [string]$Extension) {
    $full = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($full).TrimEnd('\')
    $root = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Kiosk')).TrimEnd('\')
    if (-not [string]::Equals($directory, $root, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetExtension($full), $Extension, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Kiosk data file must be a $Extension file directly in $root."
    }
    return $full
}

function Set-KioskConfigAtomically([string]$Path, [string]$Content) {
    $full = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($full)
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($full) + '.tmp-' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $directory ('.' + [IO.Path]::GetFileName($full) + '.backup-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $full) {
            if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Kiosk configuration is a junction or symbolic link: $full"
            }
            # ReplaceFile keeps the destination ACL and never exposes a partially written JSON file.
            [IO.File]::Replace($temporary, $full, $backup)
        } else { [IO.File]::Move($temporary, $full) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    }
}

function Set-KioskFileAcl([string]$Path, [switch]$UsersRead) {
    $file = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Kiosk file does not exist: $file" }
    if ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Kiosk file is a junction or symbolic link: $file" }
    $rights = @('*S-1-5-18:F', '*S-1-5-32-544:F')
    if ($UsersRead) { $rights += '*S-1-5-32-545:R' }
    & icacls.exe $file /reset | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot reset file ACL: $file" }
    & icacls.exe $file /inheritance:r /grant:r @rights | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot protect file: $file" }
}

function Invoke-KioskSc {
    & sc.exe @args
    if ($LASTEXITCODE -ne 0) { throw "Service command failed (exit $LASTEXITCODE)." }
}

function Get-KioskRegistryValue([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) { return @{ Exists = $false; Kind = ''; Value = $null } }
    $key = Get-Item -LiteralPath $Path
    try {
        if ($key.GetValueNames() -notcontains $Name) { return @{ Exists = $false; Kind = ''; Value = $null } }
        return @{ Exists = $true; Kind = [string]$key.GetValueKind($Name); Value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    } finally { $key.Close() }
}

function Open-KioskWritableRegistryKey([string]$Path) {
    $parent = Get-Item -LiteralPath $Path
    try { return $parent.OpenSubKey('', $true) }
    finally { $parent.Close() }
}

function Open-KioskRegistryJournal([string]$Component) {
    $dataRoot = Join-Path $env:ProgramData 'Kiosk'
    Set-KioskDirectoryAcl $dataRoot -UsersRead
    $directory = Join-Path $env:ProgramData 'Kiosk\DeploymentState'
    Set-KioskDirectoryAcl $directory
    $file = Join-Path $directory ($Component + '.clixml')
    if (Test-Path -LiteralPath $file) {
        Set-KioskFileAcl $file
        $journal = Import-Clixml -LiteralPath $file
    }
    else { $journal = @{ Version = 1; Values = @() } }
    return @{ File = $file; State = $journal; SecureFiles = $true }
}

function Save-KioskRegistryJournal($Journal) {
    $temporary = $Journal.File + '.tmp-' + [Guid]::NewGuid().ToString('N')
    $backup = $Journal.File + '.backup-' + [Guid]::NewGuid().ToString('N')
    try {
        $Journal.State | Export-Clixml -LiteralPath $temporary -Depth 12
        if ($Journal.SecureFiles) { Set-KioskFileAcl $temporary }
        if (Test-Path -LiteralPath $Journal.File) { [IO.File]::Replace($temporary, $Journal.File, $backup) }
        else { Move-Item -LiteralPath $temporary -Destination $Journal.File }
        if ($Journal.SecureFiles) { Set-KioskFileAcl $Journal.File }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    }
}

function Set-KioskRegistryValue($Journal, [string]$Path, [string]$Name, $Value, [string]$Kind = 'DWord', [switch]$CommaSet) {
    $entry = @($Journal.State.Values | Where-Object { $_.Path -eq $Path -and $_.Name -eq $Name }) | Select-Object -First 1
    if (-not $entry) {
        $entry = @{ Path = $Path; Name = $Name; Original = (Get-KioskRegistryValue $Path $Name); Applied = $null }
        $Journal.State.Values += $entry
    }
    # Persist both rollback data and the intended write before changing the registry.
    $entry.Applied = @{ Exists = $true; Kind = $Kind; Value = $Value }
    if ($CommaSet) { $entry.CommaSet = $true }
    Save-KioskRegistryJournal $Journal
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    if ($Name -eq '') {
        $key = Open-KioskWritableRegistryKey $Path
        try { $key.SetValue('', $Value, [Microsoft.Win32.RegistryValueKind]::$Kind) }
        finally { $key.Close() }
    }
    else { New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Kind -Force | Out-Null }
}

function Restore-KioskRegistryJournal($Journal) {
    $remaining = @()
    $entries = @($Journal.State.Values)
    [array]::Reverse($entries)
    foreach ($entry in $entries) {
        $current = Get-KioskRegistryValue $entry.Path $entry.Name
        $expected = $entry.Applied
        if ($entry.CommaSet) {
            # Different Kiosk components and device management may add provider IDs later.
            # Undo only this component's additions/removals, preserving every other ID.
            $original = @(([string]$entry.Original.Value) -split ',' | Where-Object { $_ } | ForEach-Object { $_.Trim() })
            $applied = @(([string]$expected.Value) -split ',' | Where-Object { $_ } | ForEach-Object { $_.Trim() })
            $added = @($applied | Where-Object { $original -notcontains $_ })
            $removed = @($original | Where-Object { $applied -notcontains $_ })
            $list = @(([string]$current.Value) -split ',' | Where-Object { $_ } | ForEach-Object { $_.Trim() } | Where-Object { $added -notcontains $_ })
            foreach ($id in $removed) { if ($list -notcontains $id) { $list += $id } }
            $key = Open-KioskWritableRegistryKey $entry.Path
            try {
                if ($list.Count -gt 0 -or $entry.Original.Exists) { $key.SetValue($entry.Name, ($list -join ','), [Microsoft.Win32.RegistryValueKind]::String) }
                else { $key.DeleteValue($entry.Name, $false) }
            } finally { $key.Close() }
            continue
        }
        # Hashtable property order is not stable across processes, compare fields separately.
        $matches = $current.Exists -eq $expected.Exists -and $current.Kind -eq $expected.Kind -and
            (($current.Value | ConvertTo-Json -Compress -Depth 4) -eq ($expected.Value | ConvertTo-Json -Compress -Depth 4))
        if (-not $matches) {
            Write-Warning "Policy changed after Kiosk deployment; leaving it in place: $($entry.Path)\$($entry.Name)"
            $remaining += $entry
            continue
        }
        if ($entry.Original.Exists) {
            if (-not (Test-Path -LiteralPath $entry.Path)) { New-Item -Path $entry.Path -Force | Out-Null }
            $key = Open-KioskWritableRegistryKey $entry.Path
            try { $key.SetValue($entry.Name, $entry.Original.Value, [Microsoft.Win32.RegistryValueKind]::$($entry.Original.Kind)) }
            finally { $key.Close() }
        } else {
            $key = Open-KioskWritableRegistryKey $entry.Path
            try { $key.DeleteValue($entry.Name, $false) }
            finally { $key.Close() }
        }
    }
    $Journal.State.Values = $remaining
    Save-KioskRegistryJournal $Journal
}

function Resolve-KioskKeyboardFilterKeys([object[]]$Available) {
    # These are WEKF_PredefinedKey.Id values, not the display order of pressed keys.
    $required = @('Ctrl+Alt+Del', 'Shift+Ctrl+Esc', 'Win+L')
    $resolved = @()
    foreach ($id in $required) {
        $matches = @($Available | Where-Object { $_.Id -eq $id })
        if ($matches.Count -ne 1) { throw "Keyboard Filter must expose exactly one predefined rule for $id; found $($matches.Count)." }
        $resolved += $matches[0]
    }
    return $resolved
}

#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dir = Join-Path $root 'out\test-logs'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$id = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stdout = Join-Path $dir ($id + '.stdout.log')
$stderr = Join-Path $dir ($id + '.stderr.log')
$exe = (Get-Process -Id $PID).Path
if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Cannot locate the current PowerShell executable.'
}
$arguments = '-NoProfile -NonInteractive -File "' + (Resolve-Path -LiteralPath $ScriptPath).ProviderPath + '" -TimeoutSeconds ' + $TimeoutSeconds
$proc = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$exitCode = [int]$proc.ExitCode
$proc.Dispose()
if ($exitCode -eq 0) { exit 0 }

function Fingerprint([string]$Line) {
    $x = $Line.Trim()
    $x = [regex]::Replace($x, '\b20\d\d-\d\d-\d\d[T ]\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)?\b', '<timestamp>')
    $x = [regex]::Replace($x, '(?<!\d)\d\d:\d\d:\d\d(?:\.\d+)?(?!\d)', '<time>')
    return $x
}
$items = New-Object 'System.Collections.Generic.List[string]'
$tail = New-Object 'System.Collections.Generic.List[string]'
foreach ($file in @($stderr, $stdout)) {
    foreach ($line in [IO.File]::ReadLines($file)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $tail.Add($line)
        if ($tail.Count -gt 20) { $tail.RemoveAt(0) }
        if ($line -match '(?i)\b(error|exception|failed|failure|fatal|timeout|timed out|throw)\b|:\s*error\s+[A-Za-z]+\d+' -and
            $line -notmatch '(?i)\b0\s+(errors|failed|failures)\b') {
            $items.Add($line)
        }
    }
}
if ($items.Count -eq 0) { foreach ($line in $tail) { $items.Add($line) } }
$groups = @{}
foreach ($line in $items) {
    $key = Fingerprint $line
    if (-not $groups.ContainsKey($key)) { $groups[$key] = 0 }
    $groups[$key]++
}
Write-Output ('Test failed (exit ' + $exitCode + '). Unique diagnostics: ' + $groups.Count + ', matched lines: ' + $items.Count)
foreach ($key in @($groups.Keys | Sort-Object | Select-Object -First 40)) {
    Write-Output ('[x' + $groups[$key] + '] ' + $key)
}
if ($groups.Count -gt 40) { Write-Output ('Additional distinct diagnostics: ' + ($groups.Count - 40)) }
Write-Output ('Full logs: ' + $stdout + '; ' + $stderr)
exit $exitCode

#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $payload = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($payload)) { exit 0 }
    $event = $payload | ConvertFrom-Json
    $kind = [string]$event.hook_event_name

    if ($kind -eq 'UserPromptSubmit') {
        $hint = 'Before tools: normalize the task into goal, owner, specific symbols, minimum read scope, test and rollback. For multi-file code discovery spawn code_locator on the cheap model; accept only file:line ranges and relationships. Keep the main context small. Run full tests with -ErrorsOnly.'
        @{hookSpecificOutput = @{hookEventName = 'UserPromptSubmit'; additionalContext = $hint}} | ConvertTo-Json -Compress -Depth 5
        exit 0
    }

    if ($kind -eq 'PreToolUse') {
        if ([string]$event.tool_name -ne 'Bash') { exit 0 }
        $cmd = [string]$event.tool_input.command
        if ([string]::IsNullOrWhiteSpace($cmd)) { exit 0 }

        # Rewrite only simple standalone suite invocations, not compound commands.
        if ($cmd -match '(?i)^\s*(?:&\s*)?(?:\.?[\\/])?scripts[\\/]Test\.ps1(?:\s+-TimeoutSeconds\s+\d+)?\s*$') {
            $new = $cmd.Trim() + ' -ErrorsOnly'
            @{hookSpecificOutput = @{hookEventName = 'PreToolUse'; permissionDecision = 'allow'; updatedInput = @{command = $new}}} | ConvertTo-Json -Compress -Depth 6
            exit 0
        }

        if ($cmd -match '(?i)\b(Get-Content|gc|cat|type)\b' -and
            $cmd -match '(?i)(KioskForm\.cs|SettingsView\.cs|CentralConfig\.cs|Program\.cs)' -and
            ($cmd -match '(?i)\-Raw\b' -or $cmd -notmatch '(?i)Select-Object\s+.*(-Skip|-First)|-TotalCount|-Tail\b')) {
            @{hookSpecificOutput = @{hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = 'Use rg -n to locate symbols, then bounded Get-Content | Select-Object -Skip N -First M. Avoid full source dumps.'}} | ConvertTo-Json -Compress -Depth 6
            exit 0
        }
        exit 0
    }

    if ($kind -ne 'PostToolUse' -or [string]$event.tool_name -ne 'Bash') { exit 0 }
    $cmd = [string]$event.tool_input.command
    if ($cmd -notmatch '(?i)(Test\.ps1|dotnet|rg\s|Get-WinEvent|Get-EventLog)') { exit 0 }
    $response = $event.tool_response
    if ($null -eq $response) { exit 0 }
    $raw = if ($response -is [string]) { [string]$response }
           elseif ($null -ne $response.stdout) { [string]$response.stdout + [Environment]::NewLine + [string]$response.stderr }
           elseif ($null -ne $response.output) { [string]$response.output }
           else { '' }
    if ($raw.Length -lt 5000) { exit 0 }
    $lines = @($raw -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -lt 80) { exit 0 }

    $groups = @{}
    foreach ($line in $lines) {
        $key = $line.Trim()
        $key = [regex]::Replace($key, '\b20\d\d-\d\d-\d\d[T ]\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)?\b', '<timestamp>')
        $key = [regex]::Replace($key, '(?<!\d)\d\d:\d\d:\d\d(?:\.\d+)?(?!\d)', '<time>')
        if (-not $groups.ContainsKey($key)) { $groups[$key] = 0 }
        $groups[$key]++
    }
    if ($groups.Count -ge [int]($lines.Count * 0.7)) { exit 0 }
    $summary = New-Object 'System.Collections.Generic.List[string]'
    $summary.Add('Repeated shell output: lines=' + $lines.Count + ', signatures=' + $groups.Count + '. Only timestamps/time normalized.')
    $status = $response.exit_code
    if ($null -eq $status) { $status = $response.exitCode }
    if ($null -ne $status) { $summary.Add('Original exit code: ' + [string]$status) }
    foreach ($key in @($groups.Keys | Sort-Object | Select-Object -First 45)) {
        $summary.Add('[' + $groups[$key] + 'x] ' + $key)
    }
    if ($groups.Count -gt 45) { $summary.Add('Other distinct lines: ' + ($groups.Count - 45)) }

    @{decision = 'block'; reason = 'Large repeated command output was summarized. Rerun a targeted command for original evidence.'; hookSpecificOutput = @{hookEventName = 'PostToolUse'; additionalContext = ($summary -join [Environment]::NewLine)}} | ConvertTo-Json -Compress -Depth 6
} catch {
    exit 0
}

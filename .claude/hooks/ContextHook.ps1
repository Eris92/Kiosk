#Requires -Version 5.1
# Claude Code hook: narrow search, safe test wrapper and compact repeated output.
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $inputJson = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($inputJson)) { exit 0 }
    $event = $inputJson | ConvertFrom-Json
    $kind = [string]$event.hook_event_name
    $toolName = [string]$event.tool_name

    if ($kind -eq 'UserPromptSubmit') {
        $prompt = [string]$event.prompt
        if ($prompt -match '(?i)(src[/\\].+\.(cs|cpp)|scripts[/\\].+\.ps1):\d+') { exit 0 }
        @{hookSpecificOutput = @{
            hookEventName = 'UserPromptSubmit'
            additionalContext = 'Scope the request to a module/symbol first. For multi-file discovery delegate to the project Explore (Haiku); expect path:line evidence only. Verify bounded source ranges in the main agent. Use Test.ps1 -ErrorsOnly.'
        }} | ConvertTo-Json -Depth 5 -Compress
        exit 0
    }

    if ($kind -eq 'PreToolUse') {
        if ($toolName -eq 'Read') {
            $path = [string]$event.tool_input.file_path
            $limit = $event.tool_input.limit
            if ($path -match '(?i)[\\/]src[\\/].+\.(cs|cpp)$' -and
                (Test-Path -LiteralPath $path -PathType Leaf) -and
                (Get-Item -LiteralPath $path).Length -gt 45000 -and
                ($null -eq $limit -or [int]$limit -le 0)) {
                @{hookSpecificOutput = @{
                    hookEventName = 'PreToolUse'
                    permissionDecision = 'deny'
                    permissionDecisionReason = 'Large source file: use Grep to find symbols, then Read with offset and limit. Do not load whole files.'
                }} | ConvertTo-Json -Depth 5 -Compress
            }
            exit 0
        }

        if ($toolName -notin @('PowerShell', 'Bash')) { exit 0 }
        $command = [string]$event.tool_input.command
        if ([string]::IsNullOrWhiteSpace($command)) { exit 0 }
        if ($command -match '(?i)^\s*(?:&\s*)?(?:\.?[\\/])?scripts[\\/]Test\.ps1(?:\s+-TimeoutSeconds\s+\d+)?\s*$') {
            $update = @{}
            foreach ($property in $event.tool_input.PSObject.Properties) {
                $update[$property.Name] = $property.Value
            }
            $update['command'] = $command.Trim() + ' -ErrorsOnly'
            @{hookSpecificOutput = @{
                hookEventName = 'PreToolUse'
                updatedInput = $update
            }} | ConvertTo-Json -Depth 8 -Compress
            exit 0
        }
        # Avoid unbounded stdout of very large source files.
        if ($command -match '(?i)\b(Get-Content|gc|cat|type)\b' -and
            $command -match '(?i)(KioskForm\.cs|SettingsView\.cs|CentralConfig\.cs)' -and
            ($command -match '(?i)\-Raw\b' -or $command -notmatch '(?i)(Select-Object\s+.*(-Skip|-First)|-TotalCount\b|-Tail\b|head\s+-n)')) {
            @{hookSpecificOutput = @{
                hookEventName = 'PreToolUse'
                permissionDecision = 'deny'
                permissionDecisionReason = 'Find method with rg -n, then read a bounded line range instead of the entire large source file.'
            }} | ConvertTo-Json -Depth 5 -Compress
            exit 0
        }
        exit 0
    }

    if ($kind -ne 'PostToolUse' -or $toolName -notin @('PowerShell', 'Bash')) { exit 0 }
    $command = [string]$event.tool_input.command
    if ($command -notmatch '(?i)(Test\.ps1|dotnet|rg\s|Get-WinEvent|Get-EventLog)') { exit 0 }
    $response = $event.tool_response
    if ($null -eq $response -or $null -eq $response.PSObject.Properties['stdout']) { exit 0 }
    $stdout = [string]$response.stdout
    $stderr = [string]$response.stderr
    if ($stdout.Length + $stderr.Length -lt 5000) { exit 0 }

    $lines = @(($stdout + [Environment]::NewLine + $stderr) -split '\r?\n' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -lt 80) { exit 0 }

    $groups = @{}
    foreach ($line in $lines) {
        $signature = $line.Trim()
        $signature = [regex]::Replace($signature, '\b20\d\d-\d\d-\d\d[T ]\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)?\b', '<timestamp>')
        $signature = [regex]::Replace($signature, '(?<!\d)\d\d:\d\d:\d\d(?:\.\d+)?(?!\d)', '<time>')
        if (-not $groups.ContainsKey($signature)) { $groups[$signature] = 0 }
        $groups[$signature]++
    }
    # Only reduce clearly repetitive output, never obscure many distinct failures.
    if ($groups.Count -gt 60 -or $groups.Count -gt [int]($lines.Count * 0.4)) { exit 0 }

    $parts = New-Object 'System.Collections.Generic.List[string]'
    $parts.Add('Repeated output summarized: ' + $lines.Count + ' lines, ' + $groups.Count + ' unique signatures.')
    $parts.Add('Only timestamp/time-of-day differences are normalized. Original command exit status is unchanged.')
    foreach ($key in @($groups.Keys | Sort-Object)) {
        $parts.Add('[' + $groups[$key] + 'x] ' + $key)
    }
    $copy = @{}
    foreach ($property in $response.PSObject.Properties) { $copy[$property.Name] = $property.Value }
    $copy['stdout'] = $parts -join [Environment]::NewLine
    if ($null -ne $response.PSObject.Properties['stderr']) { $copy['stderr'] = '' }
    @{hookSpecificOutput = @{
        hookEventName = 'PostToolUse'
        updatedToolOutput = $copy
    }} | ConvertTo-Json -Depth 15 -Compress
} catch {
    # A hook failure must not alter the execution or lose the tool's original output.
    exit 0
}

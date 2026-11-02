#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$failures = New-Object 'System.Collections.Generic.List[string]'
function Fail([string]$Message) { $failures.Add($Message) }

foreach ($required in @('AGENTS.md', 'CLAUDE.md', '.ai/index.md', '.ai/USAGE.md', '.claude/agents/Explore.md', '.claude/hooks/ContextHook.ps1', '.codex/hooks/BrainHook.ps1', '.codex/hooks.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { Fail "Missing: $required" }
}
$documents = @(Get-ChildItem -LiteralPath (Join-Path $root '.ai') -Recurse -Filter '*.md') + @(Get-Item -LiteralPath (Join-Path $root 'AGENTS.md'), (Join-Path $root 'CLAUDE.md'))

# Routing: the index and the module folder must agree.
$index = Get-Content -LiteralPath (Join-Path $root '.ai\index.md') -Raw -Encoding UTF8
$modules = @(Get-ChildItem -LiteralPath (Join-Path $root '.ai\modules') -Filter '*.md')
foreach ($module in $modules) {
    if ($index -notmatch [regex]::Escape('(modules/' + $module.Name + ')')) { Fail "Module not routed from index: $($module.Name)" }
}
foreach ($match in [regex]::Matches($index, '\(modules/([\w.-]+)\)')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root ('.ai\modules\' + $match.Groups[1].Value)))) { Fail "Index links a missing module: $($match.Groups[1].Value)" }
}

# Freshness: every repository path and source file name the documents mention must exist.
$tracked = @(& git -C $root ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
$names = @{}
foreach ($file in $tracked) { $names[[IO.Path]::GetFileName($file).ToLowerInvariant()] = $true }
$text = @{}
foreach ($document in $documents) {
    $content = Get-Content -LiteralPath $document.FullName -Raw -Encoding UTF8
    $text[$document.FullName] = $content
    foreach ($match in [regex]::Matches($content, '(?<![\w/.:-])(?:src|scripts|config|docs|\.ai|\.claude|\.codex)/[\w./*-]*[\w*/]')) {
        if (-not (Test-Path -Path (Join-Path $root $match.Value))) { Fail "$($document.Name): stale path $($match.Value)" }
    }
    foreach ($match in [regex]::Matches($content, '(?<![\w/.*-])[A-Z][\w.]*\.(?:cs|cpp|csproj)\b')) {
        if (-not $names.ContainsKey($match.Value.ToLowerInvariant())) { Fail "$($document.Name): stale file name $($match.Value)" }
    }
}

# Coverage: every source file and script must be reachable from a module note.
$memory = ($modules | ForEach-Object { $text[$_.FullName] }) -join "`n"
$patterns = @([regex]::Matches($memory, '[\w.-]*\*[\w.-]*\.ps1') | ForEach-Object { $_.Value })
$folders = @([regex]::Matches($memory, '(?:src|scripts)/[\w./-]+/(?=[\s:,.;)])') | ForEach-Object { $_.Value })
foreach ($file in ($tracked | Where-Object { $_ -match '^(src|scripts)/.*\.(cs|cpp|ps1|mjs)$' })) {
    $name = [IO.Path]::GetFileName($file)
    if ($memory -match ('(?<![\w-])' + [regex]::Escape($name))) { continue }
    if (@($patterns | Where-Object { $name -like $_ }).Count) { continue }
    if (@($folders | Where-Object { $file.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count) { continue }
    Fail "Not covered by any module note: $file"
}

# Claude Code wiring.
$explore = Get-Content -LiteralPath (Join-Path $root '.claude\agents\Explore.md') -Raw -Encoding UTF8
if ($explore -notmatch '(?m)^model:\s*haiku\s*$') { Fail 'Explore must run on Haiku.' }
if ($explore -match '(?m)^tools:.*(Write|Edit|Bash|PowerShell)') { Fail 'Explore must stay read-only.' }
if ((Get-Content -LiteralPath (Join-Path $root 'CLAUDE.md') -Raw -Encoding UTF8) -notmatch 'AGENTS\.md') { Fail 'CLAUDE.md must route to AGENTS.md.' }
$settings = Join-Path $root '.claude\settings.json'
$parsed = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($event in @('UserPromptSubmit', 'PreToolUse', 'PostToolUse')) {
    if ($null -eq $parsed.hooks.$event) { Fail ".claude/settings.json has no $event hook." }
}
Get-Content -LiteralPath (Join-Path $root '.codex\hooks.json') -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null

# Hook behaviour.
$hooks = @{claude = (Join-Path $root '.claude\hooks\ContextHook.ps1'); codex = (Join-Path $root '.codex\hooks\BrainHook.ps1')}
$largeSource = Join-Path $root 'src\Kiosk.Client\KioskForm.cs'
function Invoke-Hook([string]$Runtime, [hashtable]$Event) {
    $file = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($file, ($Event | ConvertTo-Json -Compress -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
        $output = & cmd.exe /d /c "powershell.exe -NoProfile -NonInteractive -File `"$($hooks[$Runtime])`" < `"$file`""
        if ($LASTEXITCODE -ne 0) { throw "Hook exited with $LASTEXITCODE." }
        return (@($output) -join "`n")
    } finally { Remove-Item -LiteralPath $file -Force }
}
function Pre([string]$Tool, [hashtable]$ToolInput) { return @{hook_event_name = 'PreToolUse'; tool_name = $Tool; tool_input = $ToolInput} }
foreach ($case in @(
    @{Name = 'unbounded Read of a large file'; Runtime = 'claude'; Event = (Pre 'Read' @{file_path = $largeSource}); Expect = '"deny"'},
    @{Name = 'bounded Read of a large file'; Runtime = 'claude'; Event = (Pre 'Read' @{file_path = $largeSource; offset = 1; limit = 20}); Expect = ''},
    @{Name = 'whole-file dump'; Runtime = 'claude'; Event = (Pre 'PowerShell' @{command = 'Get-Content src\Kiosk.Client\KioskForm.cs -Raw'}); Expect = '"deny"'},
    @{Name = 'bounded dump'; Runtime = 'claude'; Event = (Pre 'PowerShell' @{command = 'Get-Content src\Kiosk.Client\KioskForm.cs | Select-Object -Skip 5 -First 9'}); Expect = ''},
    @{Name = 'search that names a large file'; Runtime = 'claude'; Event = (Pre 'Bash' @{command = 'rg -n "type " src/Kiosk.Client/KioskForm.cs'}); Expect = ''},
    @{Name = 'full suite in Claude'; Runtime = 'claude'; Event = (Pre 'PowerShell' @{command = '.\scripts\Test.ps1'}); Expect = '-ErrorsOnly'},
    @{Name = 'full suite in Codex'; Runtime = 'codex'; Event = (Pre 'Bash' @{command = '.\scripts\Test.ps1'}); Expect = '"updatedInput"'},
    @{Name = 'compact suite'; Runtime = 'claude'; Event = (Pre 'PowerShell' @{command = '.\scripts\Test.ps1 -ErrorsOnly'}); Expect = ''},
    @{Name = 'prompt routing'; Runtime = 'claude'; Event = @{hook_event_name = 'UserPromptSubmit'; prompt = 'x'}; Expect = 'Explore'}
)) {
    $result = Invoke-Hook $case.Runtime $case.Event
    if ($case.Expect -eq '') { if ($result) { Fail "Hook case '$($case.Name)' must be silent: $result" } }
    elseif ($result -notmatch [regex]::Escape($case.Expect)) { Fail "Hook case '$($case.Name)' did not return $($case.Expect): $result" }
}
$polish = [string][char]0x017C
$repeated = (1..120 | ForEach-Object { '12:00:' + ('{0:d2}' -f ($_ % 60)) + ' zzz routine ' + $polish }) -join "`n"
$routine = (1..20 | ForEach-Object { 'aaa distinct ' + $_ }) -join "`n"
$stdout = $repeated + "`n" + $routine + "`n" + (('pad' * 700)) + "`nzzz fatal: broken build"
$post = @{hook_event_name = 'PostToolUse'; tool_name = 'Bash'; tool_input = @{command = 'dotnet build'}; tool_response = @{stdout = $stdout; stderr = ''; interrupted = $false; isImage = $false}}
$summary = Invoke-Hook 'claude' $post | ConvertFrom-Json
$kept = [string]$summary.hookSpecificOutput.updatedToolOutput.stdout
if ($kept -notmatch 'fatal: broken build') { Fail 'Output filter dropped a diagnostic past the line cap.' }
if ($kept -notmatch '\[120x\]') { Fail 'Output filter did not group repeated lines.' }
if ($kept.Length -ge $stdout.Length) { Fail 'Output filter did not shorten the output.' }
$logMatch = [regex]::Match($kept, 'Full output: (.+\.log)')
if (-not $logMatch.Success) { Fail 'Output filter did not name its full log.' }
else {
    $log = $logMatch.Groups[1].Value.Trim()
    if ((Get-Content -LiteralPath $log -Raw -Encoding UTF8).TrimEnd() -ne $stdout) { Fail 'Hook log is not the unmodified output.' }
    Remove-Item -LiteralPath $log -Force
}

# Compact test mode: quiet success, grouped failures, logs on disk, exit codes preserved.
$fixture = Join-Path $root ('out\brain-test-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test.ps1') -Destination $fixture
    [IO.File]::WriteAllText((Join-Path $fixture 'Invoke-CompactTest.ps1'), 'exit 7')
    [IO.File]::WriteAllText((Join-Path $fixture 'pass.ps1'), "param([int]`$TimeoutSeconds)`nWrite-Output 'noise'`nexit 0")
    [IO.File]::WriteAllText((Join-Path $fixture 'fail.ps1'), "param([int]`$TimeoutSeconds)`n1..5 | ForEach-Object { Write-Output ('12:00:0' + `$_ + ' error: same failure') }`nexit 7")
    $compact = Join-Path $PSScriptRoot 'Invoke-CompactTest.ps1'
    $quiet = & powershell.exe -NoProfile -NonInteractive -File $compact -ScriptPath (Join-Path $fixture 'pass.ps1')
    if ($LASTEXITCODE -ne 0 -or $quiet) { Fail "Compact mode success must be silent with exit 0 (exit $LASTEXITCODE)." }
    $grouped = (& powershell.exe -NoProfile -NonInteractive -File $compact -ScriptPath (Join-Path $fixture 'fail.ps1')) -join "`n"
    if ($LASTEXITCODE -ne 7) { Fail "Compact mode changed exit 7 to $LASTEXITCODE." }
    if ($grouped -notmatch '\[x5\] <time> error: same failure') { Fail 'Compact mode did not group repeated failures.' }
    $logs = [regex]::Match($grouped, 'Full logs: (.+?\.stdout\.log)')
    if (-not $logs.Success -or -not (Test-Path -LiteralPath $logs.Groups[1].Value)) { Fail 'Compact mode did not keep its full log.' }
    & powershell.exe -NoProfile -NonInteractive -File (Join-Path $fixture 'Test.ps1') -ErrorsOnly *> $null
    if ($LASTEXITCODE -ne 7) { Fail "Test.ps1 -ErrorsOnly changed exit 7 to $LASTEXITCODE." }
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

if ($failures.Count) {
    $failures | ForEach-Object { Write-Output $_ }
    throw "Project Brain validation failed: $($failures.Count) problem(s)."
}
Write-Output 'Project Brain validation passed.'
exit 0

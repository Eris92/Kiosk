#Requires -Version 5.1
[CmdletBinding()]
param([string]$ConfigPath = (Join-Path $PSScriptRoot '..\config\apps.json'))
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run powershell.exe -STA -File Start-SessionLauncher.ps1' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $config.Applications) { throw 'Applications list is empty.' }
$form = New-Object Windows.Forms.Form
$form.Text = 'Session applications'
$form.Size = New-Object Drawing.Size(700, 450)
$form.StartPosition = 'CenterScreen'
$panel = New-Object Windows.Forms.FlowLayoutPanel
$panel.Dock = 'Fill'
$panel.Padding = New-Object Windows.Forms.Padding(20)
$form.Controls.Add($panel)
foreach ($app in $config.Applications) {
    if (-not $app.Name -or -not [IO.Path]::IsPathRooted([string]$app.Path)) { throw 'Each application needs a name and an absolute executable path.' }
    if ([IO.Path]::GetExtension([string]$app.Path) -ine '.exe') { throw 'Only executable paths are allowed.' }
    $button = New-Object Windows.Forms.Button
    $button.Text = [string]$app.Name
    $button.Size = New-Object Drawing.Size(200, 80)
    $button.Tag = $app
    $button.Add_Click({
        param($sender, $eventArgs)
        try {
            $entry = $sender.Tag
            if (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) { throw "Executable not found: $($entry.Path)" }
            $start = @{ FilePath = [string]$entry.Path; ErrorAction = 'Stop' }
            if ($entry.Arguments) { $start.ArgumentList = [string]$entry.Arguments }
            Start-Process @start
        } catch {
            [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Application error') | Out-Null
        }
    })
    $panel.Controls.Add($button)
}
$disconnect = New-Object Windows.Forms.Button
$disconnect.Text = 'Disconnect session'
$disconnect.Size = New-Object Drawing.Size(200, 80)
$disconnect.Add_Click({
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($sessionId -eq 0 -or $env:SESSIONNAME -notlike 'RDP-*') {
        [Windows.Forms.MessageBox]::Show('This action requires an RDP user session.', 'Disconnect blocked') | Out-Null
        return
    }
    & "$env:SystemRoot\System32\tsdiscon.exe" $sessionId
    if ($LASTEXITCODE -ne 0) { [Windows.Forms.MessageBox]::Show('Session disconnect failed.', 'Error') | Out-Null }
})
$panel.Controls.Add($disconnect)
[Windows.Forms.Application]::Run($form)
$form.Dispose()

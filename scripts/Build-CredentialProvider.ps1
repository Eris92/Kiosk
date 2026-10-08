#Requires -Version 5.1
# Builds the Kiosk sign-in tile (credential provider DLL). Requires Visual Studio (Build Tools) with C++ x64.
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\out\credential-provider'))
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Install Visual Studio Build Tools with the C++ workload first.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ x64 tools not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$source = Resolve-Path (Join-Path $PSScriptRoot '..\src\Kiosk.CredentialProvider')
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$out = Resolve-Path $OutputPath
# Static CRT (/MT): LogonUI must not depend on a separately installed VC++ runtime.
$compile = "cl.exe /nologo /utf-8 /W4 /O2 /MT /EHsc /DUNICODE /D_UNICODE /LD `"$source\KioskCredentialProvider.cpp`" " +
    "/Fo`"$out\\`" /Fe`"$out\KioskCredentialProvider.dll`" /link /DEF:`"$source\KioskCredentialProvider.def`" ole32.lib shlwapi.lib advapi32.lib user32.lib"
& cmd.exe /c "`"$vcvars`" >nul && $compile"
if ($LASTEXITCODE -ne 0) { throw 'Credential provider build failed.' }
Write-Output "Build ready: $out\KioskCredentialProvider.dll"

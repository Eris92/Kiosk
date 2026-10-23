#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\out\credential-provider'))
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with the x64 C++ tools and Windows SDK.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio x64 C++ tools not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\src\Kiosk.CredentialProvider')).ProviderPath
$out = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path $out | Out-Null
# Static CRT keeps LogonUI independent of an installed VC++ runtime. Warnings fail the CI gate.
$compile = "cl.exe /nologo /utf-8 /W4 /WX /O2 /MT /EHsc /sdl /guard:cf /DUNICODE /D_UNICODE /LD `"$source\KioskCredentialProvider.cpp`" " +
    "/Fo`"$out\\`" /Fe`"$out\KioskCredentialProvider.dll`" /link /DEF:`"$source\KioskCredentialProvider.def`" /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf /INCREMENTAL:NO /Brepro ole32.lib shlwapi.lib advapi32.lib user32.lib"
# Environment changes stay in cmd.exe's child process.
& cmd.exe /d /s /c "`"`"$vcvars`" >nul && $compile`""
if ($LASTEXITCODE -ne 0) { throw 'Credential provider compile/link gate failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $out 'KioskCredentialProvider.dll') -PathType Leaf)) { throw 'Native compiler did not produce the credential provider DLL.' }
Write-Output "Native build ready: $out\KioskCredentialProvider.dll"

param([string]$OutputDirectory = '.cache/code-sandbox-probe')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'The Windows native sandbox probe requires MSVC x64 build tools.'}
$setup=Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$source=Join-Path $root 'tests/Mason.CodeChecks/SandboxNativeProbe.c'
$build=Join-Path $output 'build.cmd'
@"
@echo off
call "$setup" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
cl /nologo /W4 /O1 /Oi /GS- /Zl /DUNICODE /D_UNICODE "$source" /Fo"$output\probe.obj" /link /NODEFAULTLIB /ENTRY:main /SUBSYSTEM:CONSOLE /MACHINE:X64 /OUT:"$output\SandboxNativeProbe.exe" kernel32.lib ws2_32.lib
"@ | Set-Content -LiteralPath $build -Encoding ascii
& $env:COMSPEC /d /c $build
if($LASTEXITCODE){throw "Native sandbox probe build failed: $LASTEXITCODE"}

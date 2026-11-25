param([string]$OutputDirectory='.cache/code-quiescence-probe')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(-not $output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must remain in the repository.'}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'MSVC x64 build tools are required.'}
$setup=Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$source=Join-Path $root 'tests/Mason.CodeChecks/QuiescenceProbe.c'
$build=Join-Path $output 'build.cmd'
@"
@echo off
call "$setup" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
cl /nologo /W4 /WX /O1 /MT /GS /guard:cf /DUNICODE /D_UNICODE "$source" /Fo"$output\probe.obj" /link /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /OUT:"$output\QuiescenceProbe.exe" advapi32.lib
"@ | Set-Content -LiteralPath $build -Encoding ascii
& $env:COMSPEC /d /c $build
if($LASTEXITCODE){throw "Quiescence probe build failed: $LASTEXITCODE"}

param([string]$OutputDirectory = '.cache/code-memory-probe',[switch]$MultipleReservations,[ValidateSet(2097152,536870912)][long]$SecondReservationBytes=2097152)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(-not $output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'QA output must remain inside the repository.'}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'MSVC x64 build tools are required.'}
$setup=Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$source=Join-Path $root 'tests/Mason.CodeChecks/MemoryReservationProbe.c'
$build=Join-Path $output 'build.cmd'
$probeDefine=if($MultipleReservations){'/DQA_MULTIPLE_RESERVATIONS /DQA_SECOND_RESERVE_BYTES='+$SecondReservationBytes}else{''}
@"
@echo off
call "$setup" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
cl /nologo /W4 /WX /O1 /MT /GS /guard:cf /DUNICODE /D_UNICODE $probeDefine "$source" /Fo"$output\launcher.obj" /link /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /OUT:"$output\MemoryReservationLauncher.exe" advapi32.lib ws2_32.lib
if errorlevel 1 exit /b 1
cl /nologo /W4 /WX /O1 /MT /GS /guard:cf /DUNICODE /D_UNICODE /DROLE_SENTINEL "$source" /Fo"$output\sentinel.obj" /link /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /OUT:"$output\MemoryReservationSentinel.exe" advapi32.lib ws2_32.lib
"@ | Set-Content -LiteralPath $build -Encoding ascii
& $env:COMSPEC /d /c $build
if($LASTEXITCODE){throw "Native memory probe build failed: $LASTEXITCODE"}
Get-FileHash -LiteralPath (Join-Path $output 'MemoryReservationLauncher.exe'),(Join-Path $output 'MemoryReservationSentinel.exe') -Algorithm SHA256

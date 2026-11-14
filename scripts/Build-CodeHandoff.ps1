param([string]$OutputDirectory='.cache/code-handoff')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(-not $output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Handoff output must remain within the repository.'}
& (Join-Path $PSScriptRoot 'Build-CodeCompat.ps1') -OutputDirectory $OutputDirectory -Handoff
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$tools=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$dumpbin=Join-Path $tools.FullName 'bin/Hostx64/x64/dumpbin.exe'
$imports=& $dumpbin /imports (Join-Path $env:ProgramFiles 'Git/usr/bin/msys-2.0.dll')
if($LASTEXITCODE){throw 'Cannot inspect the installed MSYS kernel32 imports.'}
$inside=$false
$names=@(foreach($line in $imports){
    if($line -match '^\s+kernel32\.dll\s*$'){$inside=$true;continue}
    if($inside -and $line -match '^\s+\S+\.dll\s*$'){break}
    if($inside -and $line -match '^\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z_0-9]*)\s*$'){$Matches[1]}
}) | Sort-Object -Unique
if($names.Count -lt 50 -or 'VirtualAlloc' -notin $names){throw 'Unsupported MSYS kernel32 import contract.'}
$definition=Join-Path $output 'dbck.def'
@('LIBRARY dbck','EXPORTS')+@($names | ForEach-Object{if($_ -eq 'VirtualAlloc'){'    VirtualAlloc=DbcVirtualAlloc'}elseif($_ -eq 'RtlRestoreContext'){'    RtlRestoreContext=DbcRtlRestoreContext'}else{"    $_=KERNEL32.$_"}}) | Set-Content -LiteralPath $definition -Encoding ascii
$setup=Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$facade=Join-Path $root 'src/Mason.Code.Native/CodeKernelFacade.c'
$bootstrap=Join-Path $root 'src/Mason.Code.Native/CodeBashBootstrap.c'
$build=Join-Path $output 'build-handoff.cmd'
@"
@echo off
call "$setup" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
rem Materialize forwarders before linking against the CRT's kernel32 imports.
rem The SDK import library omits IdnToAscii/IdnToUnicode although the system
rem exports both; direct /DEF linking attempts to resolve those as externals.
lib /nologo /MACHINE:X64 /DEF:"$definition" /OUT:"$output\dbck.lib"
if errorlevel 1 exit /b 1
cl /nologo /LD /O2 /W4 /WX /GS /guard:cf /MT /DUNICODE /D_UNICODE "$facade" /Fo"$output\facade.obj" /link /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /GUARD:CF "$output\dbck.exp" /OUT:"$output\dbck.dll" /IMPLIB:"$output\dbck.lib" "$output\dbcw.lib"
if errorlevel 1 exit /b 1
cl /nologo /O2 /W4 /WX /GS /guard:cf /MT /DUNICODE /D_UNICODE "$bootstrap" /Fo"$output\bootstrap.obj" /link /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /CETCOMPAT /GUARD:CF /OUT:"$output\Mason.BashBootstrap.exe" "$output\dbcw.lib"
"@ | Set-Content -LiteralPath $build -Encoding ascii
& $env:COMSPEC /d /c $build
if($LASTEXITCODE){throw "Handoff native build failed: $LASTEXITCODE"}
$files=@('dbcw.dll','dbck.dll','Mason.BashBootstrap.exe') | ForEach-Object{$path=Join-Path $output $_;[pscustomobject]@{Name=$_;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;Bytes=(Get-Item -LiteralPath $path).Length}}
[pscustomobject]@{Abi=3;FrameBytes=512;Files=$files;KernelImports=$names} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'handoff-manifest.json')
$files | Format-Table

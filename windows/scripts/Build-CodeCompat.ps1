param([string]$OutputDirectory='.cache/code-compat',[switch]$Diagnostics,[switch]$Handoff)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path $PSScriptRoot -Parent
if($Handoff -and -not $PSBoundParameters.ContainsKey('OutputDirectory')){$OutputDirectory='.cache/code-handoff'}
$output=[IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
if($Handoff -and $output.Equals([IO.Path]::GetFullPath((Join-Path $repositoryRoot '.cache/code-compat')),[StringComparison]::OrdinalIgnoreCase)){throw 'The handoff variant must not replace the validated legacy helper.'}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'MSVC x64 build tools are required to build the first-party coding compatibility DLL.'}
$compilerTools=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$dumpbin=Join-Path $compilerTools.FullName 'bin/Hostx64/x64/dumpbin.exe'
$nativeExports=& $dumpbin /exports (Join-Path $env:WINDIR 'System32/ntdll.dll')
if($LASTEXITCODE){throw 'Cannot inspect the system native export contract.'}
$names=@($nativeExports | ForEach-Object {if($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z_0-9]*)(?:\s|$)'){$Matches[1]}} | Sort-Object -Unique)
$wrapped=@('NtCreateMutant','NtCreateEvent','NtCreateSemaphore','NtCreateTimer','NtCreateJobObject','NtCreateSymbolicLinkObject','NtSetInformationToken','NtSetSecurityObject')
if($Handoff){$wrapped+=@('NtCreateDirectoryObject','NtCreateSection')}
if($names.Count -lt 500 -or @($wrapped|Where-Object {$_ -notin $names}).Count){throw 'Unsupported native export contract.'}
$msys=Join-Path $env:ProgramFiles 'Git/usr/bin/msys-2.0.dll'
$runtimeImports=& $dumpbin /imports $msys
if($LASTEXITCODE){throw 'Cannot inspect the installed MSYS import contract.'}
$inNative=$false
$required=@(foreach($line in $runtimeImports){
    if($line -match '^\s+ntdll\.dll\s*$'){$inNative=$true;continue}
    if($inNative -and $line -match '^\s+\S+\.dll\s*$'){break}
    if($inNative -and $line -match '^\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z_0-9]*)\s*$'){$Matches[1]}
})
if($required.Count -lt 50 -or @($required|Where-Object {$_ -notin $names}).Count -or @($wrapped|Where-Object {$_ -notin $required}).Count){throw 'Unsupported MSYS native import contract.'}
$names=@($required|Sort-Object -Unique)
$definition=Join-Path $output 'dbcw.def'
@('LIBRARY dbcw','EXPORTS')+@($names | ForEach-Object {if($_ -in $wrapped){"    $_=Dbc$_"}else{"    $_=ntdll.$_"}}) | Set-Content -LiteralPath $definition -Encoding ascii
if($Handoff){'    DbcBrokerCall' | Add-Content -LiteralPath $definition -Encoding ascii}
$imports=Join-Path $output 'native-imports.def'
@('LIBRARY ntdll.dll','EXPORTS')+@($names | ForEach-Object {"    $_"}) | Set-Content -LiteralPath $imports -Encoding ascii
$setup=Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$source=Join-Path $repositoryRoot 'src/Mason.Code.Native/CodeCompat.c'
$build=Join-Path $output 'build.cmd'
$diagnosticFlag=if($Diagnostics){'/DMASON_COMPAT_DIAGNOSTICS'}else{''}
$handoffFlag=if($Handoff){'/DMASON_HANDOFF'}else{''}
$thunk=if($Handoff){Join-Path $output 'CodeBrokerCall.obj'}else{''}
$thunkArgument=if($Handoff){'"'+$thunk+'"'}else{''}
$assembly=Join-Path $repositoryRoot 'src/Mason.Code.Native/CodeBrokerCall.asm'
$assemble=if($Handoff){"ml64 /nologo /c /Fo`"$thunk`" `"$assembly`"`r`nif errorlevel 1 exit /b 1"}else{''}
@"
@echo off
call "$setup" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
$assemble
lib /nologo /MACHINE:X64 /DEF:"$imports" /OUT:"$output\native-imports.lib"
if errorlevel 1 exit /b 1
cl /nologo /LD /O2 /W4 /WX /GS /guard:cf /MT /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 $diagnosticFlag $handoffFlag "$source" /Fo"$output\CodeCompat.obj" /link /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /CETCOMPAT /GUARD:CF /DEF:"$definition" /OUT:"$output\dbcw.dll" /IMPLIB:"$output\dbcw.lib" $thunkArgument kernel32.lib advapi32.lib "$output\native-imports.lib"
"@ | Set-Content -LiteralPath $build -Encoding ascii
& $env:COMSPEC /d /c $build
if($LASTEXITCODE){throw "Native coding compatibility build failed: $LASTEXITCODE"}
Get-FileHash (Join-Path $output 'dbcw.dll') -Algorithm SHA256 | Select-Object Path,Hash

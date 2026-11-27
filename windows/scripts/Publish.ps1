param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,[string]$ModelEvidence='',[string]$WorkflowEvidence='',[switch]$Installer,[string]$InnoCompiler='')
$ErrorActionPreference='Stop'
$appRoot=Split-Path $PSScriptRoot -Parent
$repoRoot=Split-Path $appRoot -Parent
$commit=git -C $appRoot rev-parse --verify HEAD
if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$'){throw 'Release requires a Git source commit.'}
function Confirm-Source {
    $dirty=git -C $appRoot status --porcelain --untracked-files=all
    if($LASTEXITCODE -ne 0 -or $dirty -or (git -C $appRoot rev-parse HEAD) -cne $commit){throw 'Release requires a clean checkout at the evaluated source commit.'}
}
Confirm-Source
$configured=[xml](Get-Content -LiteralPath (Join-Path $appRoot 'src/MasonAgent/MasonAgent.csproj') -Raw)
if($configured.SelectSingleNode('/Project/PropertyGroup/Version').InnerText -cne $Version){throw "Requested version differs from the source version."}
# Model evidence is optional. A release is built from source and the scripted checks below, which need no model.
# Evidence from a local model run, when given, is checked against this commit and shipped with the release.
if([bool]$ModelEvidence -ne [bool]$WorkflowEvidence){throw 'Give both -ModelEvidence and -WorkflowEvidence, or neither.'}
$withEvidence=[bool]$ModelEvidence
if($withEvidence){
    & (Join-Path $PSScriptRoot 'Assert-ModelBenchmark.ps1') -Report $ModelEvidence -Commit $commit
    $primaryModel=(Get-Content -LiteralPath $ModelEvidence -Raw | ConvertFrom-Json).Model
    & (Join-Path $PSScriptRoot 'Assert-WorkflowQualification.ps1') -Report $WorkflowEvidence -Commit $commit -PrimaryModel $primaryModel
}
& (Join-Path $PSScriptRoot 'Prepare-LanguageTools.ps1')
$payload=Join-Path $appRoot "artifacts/MasonAgent-$Version-win-x64"
if(Test-Path -LiteralPath $payload){throw "Release already exists: $payload"}
& (Join-Path $PSScriptRoot 'Build-CodeCompat.ps1')
& (Join-Path $PSScriptRoot 'Build-CodeHandoff.ps1')
& (Join-Path $PSScriptRoot 'Build-CodeSandboxProbe.ps1')
dotnet build (Join-Path $appRoot 'tests/Mason.CodeChecks') -c Release -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1
if($LASTEXITCODE -ne 0){throw 'Coding checks build failed'}
$previousProbe=$env:MASON_SANDBOX_PROBE
$previousData=$env:MASON_DATA_HOME
try {
    $env:MASON_SANDBOX_PROBE=Join-Path $appRoot '.cache/code-sandbox-probe/SandboxNativeProbe.exe'
    if(!$previousData){$env:MASON_DATA_HOME=Join-Path $appRoot '.cache/release-test-profile'}
    dotnet run --no-build --project (Join-Path $appRoot 'tests/Mason.CodeChecks') -c Release
    if($LASTEXITCODE -ne 0){throw 'Full coding harness regression suite failed'}
    dotnet run --no-build --project (Join-Path $appRoot 'tests/Mason.CodeChecks') -c Release -- --extension-checks (Join-Path ([IO.Path]::GetTempPath()) ('mason-extensions-'+[guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'Harness extension checks failed'}
    dotnet run --no-build --project (Join-Path $appRoot 'tests/Mason.CodeChecks') -c Release -- --modern-checks (Join-Path ([IO.Path]::GetTempPath()) ('mason-modern-'+[guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'Repository search, Git, steering, rewind or investigation regression'}
    dotnet run --no-build --project (Join-Path $appRoot 'tests/Mason.CodeChecks') -c Release -- --checkout-controls (Join-Path ([IO.Path]::GetTempPath()) ('mason-checkout-'+[guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'Positive/negative checkout control matrix failed'}
} finally {$env:MASON_SANDBOX_PROBE=$previousProbe;$env:MASON_DATA_HOME=$previousData}
dotnet publish (Join-Path $appRoot 'src/MasonAgent/MasonAgent.csproj') -c Release -p:DebugType=None -r win-x64 --self-contained true -p:Platform=x64 -p:PlaywrightPlatform=win-x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:Version=$Version -o $payload -m:1
if($LASTEXITCODE -ne 0){throw 'App publish failed'}
$cliOutput=Join-Path $appRoot '.cache/cli-publish'
dotnet publish (Join-Path $appRoot 'src/Mason.Cli') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o $cliOutput
if($LASTEXITCODE -ne 0){throw 'Headless CLI publish failed'}
foreach($name in 'mason.exe','mason.dll','mason.deps.json','mason.runtimeconfig.json'){
    Copy-Item -LiteralPath (Join-Path $cliOutput $name) -Destination $payload
}
& (Join-Path $payload 'mason.exe') --help
if($LASTEXITCODE -ne 0){throw 'Published CLI failed startup'}
& (Join-Path $PSScriptRoot 'Build-CliLauncher.ps1') -Payload $payload
& python (Join-Path $appRoot 'tests/terminal/reattach.py') (Join-Path $payload 'mason.exe') (Join-Path ([IO.Path]::GetTempPath()) ('mason-task-client-'+[guid]::NewGuid().ToString('N')))
if($LASTEXITCODE -ne 0){throw 'Packaged detached-task client failed reattachment.'}
dotnet publish (Join-Path $appRoot 'src/Mason.SandboxSetup') -c Release -p:DebugType=None -r win-x64 --self-contained true -p:Version=$Version -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o (Join-Path $payload 'sandbox-setup')
if($LASTEXITCODE -ne 0){throw 'Mason.SandboxSetup publish failed'}
& (Join-Path $PSScriptRoot 'Assert-ReleasePayload.ps1') -Payload $payload
Confirm-Source
if($withEvidence){
    Copy-Item -LiteralPath $ModelEvidence -Destination (Join-Path $payload 'model-benchmark.json')
    Copy-Item -LiteralPath $WorkflowEvidence -Destination (Join-Path $payload 'workflow-evidence.json')
    & (Join-Path $PSScriptRoot 'Assert-ModelBenchmark.ps1') -Report "$payload/model-benchmark.json" -Commit $commit
    & (Join-Path $PSScriptRoot 'Assert-WorkflowQualification.ps1') -Report "$payload/workflow-evidence.json" -Commit $commit -PrimaryModel (Get-Content "$payload/model-benchmark.json" -Raw | ConvertFrom-Json).Model
}
if(!([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'MasonAgent.dll')).ProductVersion).EndsWith("+$commit")){throw 'Published app has the wrong source revision.'}
$source=[ordered]@{Version=$Version;Commit=$commit;Tree=(git -C $appRoot rev-parse 'HEAD^{tree}')}
if($withEvidence){$source.BenchmarkSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'model-benchmark.json')).Hash;$source.WorkflowSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'workflow-evidence.json')).Hash}
[pscustomobject]$source | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'release-source.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $payload
$notices=Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
if(Test-Path -LiteralPath $notices){Copy-Item -LiteralPath $notices -Destination $payload}
New-Item -ItemType Directory -Path (Join-Path $payload 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $appRoot 'src/MasonAgent/Assets/Fonts/LICENSE-Manrope.txt') -Destination (Join-Path $payload 'licenses')
if(-not(Test-Path -LiteralPath (Join-Path $payload 'MasonAgent.exe')) -or -not(Test-Path -LiteralPath (Join-Path $payload 'MasonAgent.pri'))){throw 'Executable or WinUI resources missing'}
# Mason Agent never ships the Postgres provider, and always ships its native coding handoff and sandbox setup.
if(Test-Path -LiteralPath (Join-Path $payload 'Npgsql.dll')){throw 'Database provider leaked into Mason Agent'}
foreach($file in @('native-handoff/dbcw.dll','native-handoff/dbck.dll','native-handoff/Mason.BashBootstrap.exe','native-handoff/handoff-manifest.json','Mason.NetworkHook.dll','sandbox-setup/Mason.SandboxSetup.exe')){if(-not(Test-Path -LiteralPath (Join-Path $payload $file))){throw "Coding dependency missing: $file"}}
Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object{[pscustomobject]@{Path=$_.FullName.Substring($payload.Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Encoding utf8
Compress-Archive -LiteralPath $payload -DestinationPath "$payload.zip" -CompressionLevel Optimal
if($Installer){
    if(-not(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)){throw 'Provide an existing Inno Setup compiler with -InnoCompiler.'}
    & $InnoCompiler "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputPath=$appRoot/artifacts" (Join-Path $appRoot 'packaging/MasonAgent.iss')
    if($LASTEXITCODE -ne 0){throw 'Installer compile failed'}
}
Write-Output "Published Mason Agent: $payload"

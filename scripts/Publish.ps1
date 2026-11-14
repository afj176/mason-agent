param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,[switch]$Installer,[string]$InnoCompiler='')
$ErrorActionPreference='Stop'
$appRoot=Split-Path $PSScriptRoot -Parent
$payload=Join-Path $appRoot "artifacts/MasonAgent-$Version-win-x64"
if(Test-Path -LiteralPath $payload){throw "Release already exists: $payload"}
& (Join-Path $PSScriptRoot 'Build-CodeHandoff.ps1')
if($LASTEXITCODE -ne 0){throw 'Native coding handoff build failed'}
dotnet publish (Join-Path $appRoot 'src/MasonAgent/MasonAgent.csproj') -c Release -p:DebugType=None -r win-x64 --self-contained true -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:Version=$Version -o $payload -m:1
if($LASTEXITCODE -ne 0){throw 'App publish failed'}
dotnet publish (Join-Path $appRoot 'src/Mason.SandboxSetup') -c Release -p:DebugType=None -r win-x64 --self-contained true -p:Version=$Version -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o (Join-Path $payload 'sandbox-setup')
if($LASTEXITCODE -ne 0){throw 'Mason.SandboxSetup publish failed'}
& (Join-Path $PSScriptRoot 'Assert-ReleasePayload.ps1') -Payload $payload
Copy-Item -LiteralPath (Join-Path $appRoot 'README.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $appRoot 'LICENSE') -Destination $payload
$notices=Join-Path $appRoot 'THIRD-PARTY-NOTICES.md'
if(Test-Path -LiteralPath $notices){Copy-Item -LiteralPath $notices -Destination $payload}
New-Item -ItemType Directory -Path (Join-Path $payload 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $appRoot 'brand/logo-kit/fonts/LICENSE-Manrope.txt') -Destination (Join-Path $payload 'licenses')
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

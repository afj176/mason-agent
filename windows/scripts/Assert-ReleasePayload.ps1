param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference='Stop'
$payloadRoot=(Get-Item -LiteralPath $Payload).FullName
if(-not (Test-Path -LiteralPath $payloadRoot -PathType Container)){throw 'Release payload must be a directory.'}

# Both the portable ZIP and installer must be built from application files only.
# Never let a validation run's runtime choices become another user's defaults.
$forbidden=@(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force | Where-Object {
    $_.Name -in @('model-library.json','model-library.json.tmp','ai-connections.json','chat-library.json','code-projects.json','code-projects.json.tmp','appearance.json','model-downloads.json','SandboxNativeProbe.exe') -or
    ($_.PSIsContainer -and $_.Name -in @('code-sessions','code-webview','chat-files','chat-webview','sandbox-tools','.mason-sandbox','.dbcowboy-sandbox')) -or
    ($_.PSIsContainer -and $_.Parent.FullName -eq $payloadRoot -and $_.Name -in @('data','ui-qa-data')) -or
    (-not $_.PSIsContainer -and ($_.Extension -in @('.gguf','.safetensors') -or $_.Name -match '^(pytorch_model(?:-\d+-of-\d+)?|adapter_model)\.bin$'))
})
if($forbidden.Count){
    $paths=($forbidden | ForEach-Object {$_.FullName.Substring($payloadRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).Length+1)}) -join ', '
    throw "Release payload contains user runtime settings or profile data: $paths"
}
Write-Output 'PASS release payload contains no runtime settings or profile data'
if(!(Test-Path -LiteralPath (Join-Path $payloadRoot 'System.Security.Cryptography.ProtectedData.dll') -PathType Leaf)){throw 'Missing protected background-connection dependency.'}
foreach($file in 'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Core.Projection.dll','WebView2Loader.dll'){
    if(!(Test-Path -LiteralPath (Join-Path $payloadRoot $file) -PathType Leaf)){throw "Missing embedded Website viewer dependency: $file"}
}
foreach ($file in @('Microsoft.Playwright.dll','.playwright/node/win32_x64/node.exe','.playwright/node/LICENSE','.playwright/package/cli.js','.playwright/package/LICENSE')) {
    if (!(Test-Path -LiteralPath (Join-Path $payloadRoot $file) -PathType Leaf)) { throw "Missing browser verification dependency: $file" }
}
$platforms = @(Get-ChildItem -LiteralPath (Join-Path $payloadRoot '.playwright/node') -Directory)
if ($platforms.Count -ne 1 -or $platforms[0].Name -ne 'win32_x64') { throw 'The Windows payload must include only the Windows x64 browser driver.' }

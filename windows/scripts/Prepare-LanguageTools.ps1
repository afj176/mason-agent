$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$config=Join-Path $PSScriptRoot 'language-tools'
$target=Join-Path $repo '.cache/language-tools-v2'
$env:PIP_DISABLE_PIP_VERSION_CHECK='1'
dotnet build (Join-Path $repo 'src/Mason.WindowHost') -c Release -m:1 -p:NuGetAudit=false -p:UseSharedCompilation=false -o (Join-Path $repo '.cache/window-host')
if($LASTEXITCODE){throw 'Native window-control helper build failed.'}
& npm.cmd ci --prefix $config --ignore-scripts --no-audit --no-fund
if($LASTEXITCODE){throw 'TypeScript dependency preparation failed.'}
$compiler=Join-Path $config 'node_modules/typescript'
$ts=New-Item -ItemType Directory -Path (Join-Path $target 'typescript') -Force
Copy-Item -LiteralPath (Join-Path $compiler 'lib/typescript.js') -Destination $ts.FullName -Force
Get-ChildItem (Join-Path $compiler 'lib') -Filter '*.d.ts'|Where-Object Name -like 'lib.*'|Copy-Item -Destination $ts.FullName -Force
Copy-Item -LiteralPath (Join-Path $compiler 'LICENSE.txt') -Destination $ts.FullName -Force
$wheels=Join-Path $repo '.cache/language-wheels'
& python -m pip download --only-binary=:all: --require-hashes --no-deps --dest $wheels -r (Join-Path $config 'requirements.txt')
if($LASTEXITCODE){throw 'Python dependency verification failed.'}
& python -m pip install --no-index --find-links $wheels --no-deps --require-hashes --upgrade --no-compile --target (Join-Path $target 'python') -r (Join-Path $config 'requirements.txt')
if($LASTEXITCODE){throw 'Python dependency preparation failed.'}

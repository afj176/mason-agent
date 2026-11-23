param([Parameter(Mandatory)][string]$Endpoint,[Parameter(Mandatory)][string]$Model,[Parameter(Mandatory)][string]$OutputDirectory,[ValidateRange(2,10)][int]$Repetitions=2)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$server=[uri]$Endpoint
if(!$server.IsLoopback -or $server.Scheme -notin @('http','https')){throw 'Use the loopback endpoint of a dedicated local model.'}
$commit=git -C $root rev-parse --verify HEAD
if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$' -or (git -C $root status --porcelain --untracked-files=all)){throw 'Benchmark the committed release from a clean checkout.'}
dotnet run --project (Join-Path $root 'tests/Mason.CodeChecks') -c Release -p:UseSharedCompilation=false -p:NuGetAudit=false -- --model-benchmark $Endpoint $Model $OutputDirectory $Repetitions $commit
if($LASTEXITCODE -ne 0){throw "The real-model qualification failed. Inspect $OutputDirectory/report.json."}
& (Join-Path $PSScriptRoot 'Assert-ModelBenchmark.ps1') -Report (Join-Path $OutputDirectory 'report.json') -Commit $commit

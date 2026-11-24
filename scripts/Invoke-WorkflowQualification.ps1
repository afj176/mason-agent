param(
    [Parameter(Mandatory)][string]$Endpoint, [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][string]$SecondaryEndpoint, [Parameter(Mandatory)][string]$SecondaryModel,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path $repo 'tests/fixtures/bear-store'
$commit = git -C $repo rev-parse --verify HEAD
function Assert-Source {
    if ($commit -notmatch '^[a-f0-9]{40}$' -or (git -C $repo rev-parse HEAD) -cne $commit -or
        (git -C $repo status --porcelain --untracked-files=all) -or $LASTEXITCODE) { throw 'Qualify a clean, committed checkout.' }
}
Assert-Source
function Get-ModelIdentity([uri]$Server, [string]$Id) {
    if (!$Server.IsLoopback -or $Server.Scheme -notin 'http','https') { throw 'Qualification requires a local model endpoint.' }
    $base = $Server.AbsoluteUri.TrimEnd('/')
    $available = Invoke-RestMethod "$base/v1/models" -TimeoutSec 15
    if (@($available.data.id) -cnotcontains $Id) { throw "Model $Id is unavailable at $base." }
    $props = Invoke-RestMethod "$base/props" -TimeoutSec 15
    $weights = [IO.Path]::GetFileName($props.model_path)
    if (!$weights) { throw "The model server at $base did not identify its weights." }
    $vision=$props.PSObject.Properties['modalities'] -and $props.modalities.PSObject.Properties['vision'] -and $props.modalities.vision -eq $true
    [pscustomobject]@{ Id=$Id; Weights=$weights; Vision=[bool]$vision }
}
$models = @((Get-ModelIdentity $Endpoint $Model), (Get-ModelIdentity $SecondaryEndpoint $SecondaryModel))
if ($Model -eq $SecondaryModel -or $models[0].Weights -eq $models[1].Weights) { throw 'Use two distinct local models.' }
$output = [IO.Path]::GetFullPath($OutputDirectory,(Get-Location).Path)
if (Test-Path -LiteralPath $output) { throw 'Each qualification requires a fresh directory.' }
$null = New-Item -ItemType Directory $output
dotnet build (Join-Path $repo 'src/Mason.WindowHost') -c Release -o (Join-Path $repo '.cache/window-host') -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1
if($LASTEXITCODE){throw 'Could not build the native window helper for this revision.'}
foreach ($build in @(@('cli','src/Mason.Cli','mason'),@('checks','tests/Mason.CodeChecks','Mason.CodeChecks'))) {
    $bin = Join-Path $output $build[0]
    dotnet build (Join-Path $repo $build[1]) -c Release -o $bin -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1
    if ($LASTEXITCODE) { throw "Build failed for $($build[1])" }
    foreach ($dll in "$($build[2]).dll",'Mason.Code.Core.dll') {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin $dll)).ProductVersion
        if (!$version.EndsWith("+$commit")) { throw "Wrong source revision in $dll" }
    }
}
$cli = Join-Path $output 'cli/mason.exe'
$node = Join-Path $output 'cli/.playwright/node/win32_x64/node.exe'
function Read-Results($Plan) {
    foreach ($r in $Plan.Results) { [pscustomobject]@{Target=$r.Check.Target; Passed=$r.Passed} }
}
function Invoke-StoreBuild($Server,$Id,$Area,$Brief,$Round) {
    $workspace = (New-Item -ItemType Directory "$Area/$Brief-$Round/project" -Force).FullName
    $trial = Split-Path $workspace -Parent
    $initial = @(Get-ChildItem -LiteralPath $workspace -Force).Count
    & $cli exec --workspace $workspace --profile "$trial/profile" --prompt-file "$fixtures/$Brief.txt" --endpoint $Server --model $Id --auto *> "$trial/run.jsonl"
    $code = $LASTEXITCODE
    if ($code) { throw "Store generation failed. See $trial/run.jsonl" }
    $sessions = @(Get-ChildItem "$trial/profile/code-sessions/*.json")
    if ($sessions.Count -ne 1) { throw "Expected one fresh session in $trial." }
    $session = Get-Content -LiteralPath $sessions[0].FullName -Raw | ConvertFrom-Json
    $plan = @($session.Turns | Where-Object Acceptance)[0].Acceptance
    & $node "$fixtures/edge-store-smoke.cjs" $workspace $trial "$output/cli" *> "$trial/edge.log"
    if ($LASTEXITCODE) { throw "Independent Edge verification failed. See $trial/edge.log" }
    [pscustomobject]@{
        Model=$Id; Brief=$Brief; Round=$Round; InitialEntries=$initial; Mode=$session.Permission; Exit=$code; State=$session.State;
        Files=@(Get-ChildItem $workspace -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($workspace,$_.FullName) });
        Changes=@($session.Changes | ForEach-Object Path | Sort-Object -Unique); Revision=$plan.VerifiedRevision;
        ReviewPage=$plan.IndependentReviewPage; Checks=@(Read-Results $plan); Edge=(Get-Content "$trial/edge-smoke.json" -Raw | ConvertFrom-Json)
    }
}
$controlPrompt='Honey costs $10 each, or $9 each if the cart contains three or more Honey. Berries cost $5 and Salmon $15 each. Show the shelf prices for all three products. Checkout must clear the cart and issue a new ORD- receipt that persists after reload.'
$mutations = [ordered]@{
    working=@('','')
    quantity=@('cart[item] >= 3 ? 9 : 10','cart[item] >= 3 ? 9 : (cart[item] === 2 ? 20 : 10)')
    mixed=@('itemPrice = 15;','itemPrice = cart.honey > 0 ? 5 : 15;')
    shelf=@('$15 per fish','$5 per fish')
    uncleared=@('cart[item] = 0;','cart[item] = cart[item];')
    receipt=@("localStorage.setItem('final-order-number', orderNumber);","sessionStorage.removeItem('unused');")
    reused=@("'ORD-' + Math.random().toString(36).substr(2, 9)","'ORD-fixed'")
}
$baseline = Get-Content "$fixtures/index.html" -Raw
function Invoke-ReviewControl($Server,$Id,$Area,$Case) {
    $workspace = (New-Item -ItemType Directory "$Area/control-$Case/project" -Force).FullName
    $before,$after = $mutations[$Case]
    $page = $baseline
    if ($before) {
        if ([regex]::Matches($page,[regex]::Escape($before)).Count -ne 1) { throw "Ambiguous fixture mutation for $Case." }
        $page=$page.Replace($before,$after)
    }
    [IO.File]::WriteAllText("$workspace/index.html",$page)
    $log = Join-Path (Split-Path $workspace -Parent) 'review.json'
    & $cli review --workspace $workspace --endpoint $Server --model $Id --prompt $controlPrompt *> $log
    $code = $LASTEXITCODE
    $review = Get-Content $log -Raw | ConvertFrom-Json
    if ($review.type -cne 'review.result') { throw "Missing independent review for $Case." }
    [pscustomobject]@{Model=$Id; Case=$Case; Exit=$code; Checks=@(Read-Results $review.data); Revision=$review.data.VerifiedRevision}
}
$builds=@()
$controls=@()
$repairs=@()
$doubleCheckouts=@()
$workers=@()
$visual=$null
foreach ($index in 0,1) {
    $server=@($Endpoint,$SecondaryEndpoint)[$index]
    $id=$models[$index].Id
    $area=Join-Path $output "model-$index"
    & "$output/checks/Mason.CodeChecks.exe" --double-checkout $server $id "$area/double"
    if ($LASTEXITCODE) { throw "Cross-layer checkout failed for $id. See $area/double." }
    $doubleCheckouts+=Get-Content "$area/double/double-checkout.json" -Raw | ConvertFrom-Json
    & "$output/checks/Mason.CodeChecks.exe" --worker-check $server $id "$area/worker"
    if ($LASTEXITCODE) { throw "Delegated investigation failed for $id." }
    $workers+=Get-Content "$area/worker/worker.json" -Raw | ConvertFrom-Json
    if($models[$index].Vision -and !$visual){
        & "$output/checks/Mason.CodeChecks.exe" --visual-check $server $id "$area/visual.json"
        if($LASTEXITCODE){throw "Native-window visual qualification failed for $id."}
        $visual=Get-Content "$area/visual.json" -Raw | ConvertFrom-Json
    }
    foreach ($round in 1,2) {
        foreach ($brief in 'brief','plain-rules') {
            Write-Host "Building $brief (round $round) with $id"
            $builds+=Invoke-StoreBuild $server $id $area $brief $round
        }
    }
    foreach ($case in $mutations.Keys) {
        Write-Host "Reviewing $case with $id"
        $controls+=Invoke-ReviewControl $server $id $area $case
    }
    & "$output/checks/Mason.CodeChecks.exe" --concurrent-repair $server $id "$area/repair"
    if ($LASTEXITCODE) { throw "Concurrent repair failed for $id. See $area/repair." }
    $repairs+=Get-Content "$area/repair/result.json" -Raw | ConvertFrom-Json
}
Assert-Source
$report=Join-Path $output 'workflow-evidence.json'
[ordered]@{ Version=2; Commit=$commit; Models=$models; Builds=$builds; Controls=$controls; Repairs=$repairs; DoubleCheckouts=$doubleCheckouts; Workers=$workers; NativeVision=$visual } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding utf8
& "$PSScriptRoot/Assert-WorkflowQualification.ps1" -Report $report -Commit $commit -PrimaryModel $Model

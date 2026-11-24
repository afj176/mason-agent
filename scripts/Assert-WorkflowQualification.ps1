param([Parameter(Mandatory)][string]$Report,[Parameter(Mandatory)][string]$Commit,[Parameter(Mandatory)][string]$PrimaryModel)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$r=Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
function Assert-That([bool]$Condition,[string]$Message) {
    if (!$Condition) { throw "Workflow qualification failed: $Message" }
}
function Assert-Checks($Checks,[string[]]$Targets,[string]$ExpectedFailure,[string]$Label) {
    Assert-That ($Checks.Count -eq $Targets.Count) "$Label check count"
    foreach ($target in $Targets) {
        $match=@($Checks | Where-Object Target -CEQ $target)
        Assert-That ($match.Count -eq 1 -and $match[0].Passed -is [bool] -and
            $match[0].Passed -eq ($target -cne $ExpectedFailure)) "$Label / $target"
    }
}
Assert-That ($r.Version -eq 3 -and $Commit -cmatch '^[0-9a-f]{40}$' -and $r.Commit -ceq $Commit) 'source identity'
Assert-That ($r.Models.Count -eq 2 -and $r.Models[0].Id -ceq $PrimaryModel -and
    $r.Models[0].Id -ne $r.Models[1].Id -and $r.Models[0].Weights -ne $r.Models[1].Weights) 'two distinct model identities'
Assert-That ($r.Builds.Count -eq 8 -and $r.Controls.Count -eq 14 -and $r.Repairs.Count -eq 2) 'matrix size'
$reviews=@('independent-basket','independent-shelf','independent-receipt')
$buildChecks=@('offer-0','offer-1','offer-2','discount-0','order')+$reviews
$failures=@{working=''; quantity=$reviews[0]; mixed=$reviews[0]; shelf=$reviews[1]; uncleared=$reviews[2]; receipt=$reviews[2]; reused=$reviews[2]}
$profiles=@('single','paired')
Assert-That ($r.RoleComparison.Count -eq 2 -and @($r.RoleComparison.startingRevision|Select-Object -Unique).Count -eq 1) 'identical role comparison inputs'
foreach($profile in $profiles){
    $run=@($r.RoleComparison|Where-Object configuration -ceq $profile)
    Assert-That ($run.Count -eq 1 -and $run[0].model -ceq $PrimaryModel -and $run[0].state -ceq 'Completed' -and $run[0].browserPassed -eq $true -and
        $run[0].elapsedSeconds -gt 0 -and $run[0].checks.Count -gt 0 -and @($run[0].checks|Where-Object { !$_.Passed }).Count -eq 0) "role comparison $profile"
    if($profile -eq 'paired'){
        Assert-That (@($r.Models|Where-Object { $_.Vision -and $_.Id -ceq $run[0].visualModel }).Count -eq 1 -and $run[0].visualEvidence.Count -gt 0) 'visual verifier observation'
        $observation=$run[0].visualEvidence[-1]|ConvertFrom-Json
        Assert-That ($observation.VisualModel -ceq $run[0].visualModel -and $observation.Hash -match '^[0-9a-f]{64}$' -and $observation.Observation.Length -gt 0) 'visual evidence identity'
    }
}
Assert-That ($r.Maintenance.Count -eq 16) 'two models and two repetitions of four maintenance cases'
foreach($case in 'regression','refactor','interruption','release-site'){
    $group=@($r.Maintenance|Where-Object case -ceq $case)
    Assert-That (@($group.revision|Select-Object -Unique).Count -eq 1) "same checkout for $case"
    foreach($identity in $r.Models){
        foreach($trial in 1,2){
            $row=@($group|Where-Object { $_.model -ceq $identity.Id -and $_.replica -eq $trial })
            Assert-That ($row.Count -eq 1 -and $row[0].revision -match '^[0-9a-f]{64}$' -and $row[0].oraclePassed -is [bool] -and
                $row[0].seconds -gt 0 -and $row[0].humanInterventions -eq 0 -and !$row[0].falseCompletion -and
                ($row[0].state -cne 'Completed' -or $row[0].oraclePassed)) "maintenance $case trial $trial for $($identity.Id)"
            if($case -eq 'release-site'){Assert-That ($row[0].sourceCommit -ceq 'd64948f5c7760a56b00bc5ce48a2e76a244bca62') 'historical checkout'}
        }
    }
    if($case -eq 'interruption'){
        Assert-That (@($group|Where-Object { $_.model -ceq $PrimaryModel -and $_.interrupted }).Count -gt 0) 'real interrupted primary run'
    }
    if($case -eq 'regression'){
        Assert-That (@($group|Where-Object { $_.model -ceq $PrimaryModel -and $_.state -ceq 'Completed' -and $_.oraclePassed }).Count -gt 0) 'verified primary maintenance repair'
    }
}
foreach ($identity in $r.Models) {
    $id=$identity.Id
    $double=@($r.DoubleCheckouts|Where-Object Model -CEQ $id)
    Assert-That ($double.Count -eq 1 -and $double[0].Calls -gt 0 -and $double[0].State -ceq 'Completed') "$id double submission repair"
    $d=$double[0]
    foreach ($field in 'CaughtDuplicate','CaughtReusedOrder','StaleRejected','FreshHashUsed','LabelPreserved','UntouchedPage','SourceReviewed','Verified','DatabaseIsolated','HoldoutPassed'){
        Assert-That ($d.$field -is [bool] -and $d.$field) "$id cross-layer checkout / $field"
    }
    Assert-That ($d.Baseline.Contains('observed 2 committed rows') -and $d.Holdout.Contains('response_count POST /checkout = 4') -and $d.Holdout.Contains('sqlite_count orders.sqlite#orders = 2') -and $d.Holdout.Contains('390px and 1280px')) "$id independent two-basket count"
    $main=@($d.Results|Where-Object {$_.Check.Target -ceq 'double' -and $_.Passed -eq $true})
    Assert-That ($main.Count -eq 1 -and $main[0].Detail.Contains('2 responses matched ORD-') -and $main[0].Detail.Contains('sqlite_count orders.sqlite#orders = 1')) "$id repeated receipt and single committed row"
    Assert-That ($d.CollapsedControl.Contains('expected 2, observed 1 committed rows') -and @($d.Results|Where-Object {$_.Check.Target -ceq 'separate' -and $_.Passed -eq $true -and $_.Detail.Contains('sqlite_count orders.sqlite#orders = 2')}).Count -eq 1) "$id cross-basket order reuse"
    $jobs=@($r.Workers|Where-Object Model -CEQ $id)
    Assert-That ($jobs.Count -eq 1 -and $jobs[0].Calls -gt 0 -and $jobs[0].SourceHash -cmatch '^[a-f0-9]{64}$') "$id delegated worker"
    $w=$jobs[0]
    Assert-That ($w.ReceivedFinding -eq $true) "$id parent received worker result"
    Assert-That (@($w.Findings|Where-Object {$_.Path -ceq 'policy.js' -and $_.Line -eq 2 -and $_.Quote.Contains([string]$w.Limit) -and $_.Answer.Contains([string]$w.Limit)}).Count -gt 0) "$id worker source citation"
    Assert-That (![string]::IsNullOrWhiteSpace($id) -and ![string]::IsNullOrWhiteSpace($identity.Weights)) 'model identity'
    foreach ($round in 1,2) {
        foreach ($brief in 'brief','plain-rules') {
            $label="$id / $brief / $round"
            $rows=@($r.Builds | Where-Object { $_.Model -ceq $id -and $_.Round -eq $round -and $_.Brief -ceq $brief })
            Assert-That ($rows.Count -eq 1) "$label missing or duplicate"
            $b=$rows[0]
            Assert-That ($b.Mode -eq 3 -and $b.InitialEntries -eq 0 -and $b.Exit -eq 0 -and $b.State -ceq 'Completed' -and
                $b.Files.Count -eq 1 -and $b.Files[0] -ceq 'index.html' -and $b.Changes.Count -eq 1 -and $b.Changes[0] -ceq 'index.html' -and
                $b.ReviewPage -ceq 'index.html' -and $b.Revision -cmatch '^[0-9a-f]{64}$') "$label workspace or completion receipt"
            Assert-Checks $b.Checks $buildChecks '' $label
            Assert-That ($b.Edge.edge -match '^\d+\.' -and $b.Edge.run.Count -eq 2) "$label Edge version or run count"
            foreach ($width in 390,1280) {
                $views=@($b.Edge.run | Where-Object width -EQ $width)
                Assert-That ($views.Count -eq 1) "$label / $width px missing or duplicate"
                $view=$views[0]
                Assert-That (($view.totals -join ',') -ceq '10,20,27,36,46,76,0,15,0') "$label cart totals"
                $shelf=@($view.shelf | ForEach-Object { $_ -join '=' })
                Assert-That (($shelf -join ',') -ceq 'Honey=10,Berries=5,Salmon=15') "$label shelf prices"
                Assert-That ($view.receipts.Count -eq 2 -and $view.receipts[0] -cmatch '^ORD-[\w-]+$' -and
                    $view.receipts[1] -cmatch '^ORD-[\w-]+$' -and $view.receipts[0] -cne $view.receipts[1]) "$label checkout receipts"
            }
        }
    }
    foreach ($case in $failures.Keys) {
        $rows=@($r.Controls | Where-Object { $_.Model -ceq $id -and $_.Case -ceq $case })
        Assert-That ($rows.Count -eq 1) "$id / $case missing or duplicate"
        $control=$rows[0]
        $expectedExit=if ($case -eq 'working') {0} else {2}
        Assert-That ($control.Exit -eq $expectedExit -and $control.Revision -cmatch '^[0-9a-f]{64}$') "$id / $case verdict"
        Assert-Checks $control.Checks $reviews $failures[$case] "$id / $case"
    }
    $rows=@($r.Repairs | Where-Object Model -CEQ $id)
    Assert-That ($rows.Count -eq 1) "$id repair missing or duplicate"
    $repair=$rows[0]
    Assert-That ($repair.State -ceq 'Completed' -and $repair.Calls -gt 0) "$id model repair"
    foreach ($field in 'InitialFailure','StaleRejected','FreshHashUsed','UntouchedFiles','ReviewPassed','OraclePass') {
        Assert-That ($repair.$field -is [bool] -and $repair.$field) "$id repair / $field"
    }
    $hashes=@($repair.InitialHash,$repair.ConcurrentHash,$repair.FinalHash)
    Assert-That (@($hashes | Sort-Object -Unique).Count -eq 3 -and @($hashes | Where-Object {$_ -cnotmatch '^[0-9a-f]{64}$'}).Count -eq 0) "$id repair revision chain"
}
$v=$r.NativeVision
Assert-That ($null -ne $v -and @($r.Models|Where-Object {$_.Vision -and $_.Id -ceq $v.Model}).Count -eq 1) 'vision model identity'
Assert-That ($v.Click.Action -ceq 'click' -and $v.Drag.Action -ceq 'drag' -and $v.StaleRejected -eq $true -and $v.NativeState -ceq 'Canvas drag completed' -and $v.Final.Length -gt 0) 'observed native transitions'
Assert-That ($v.FrameHashes.Count -eq 3 -and @($v.FrameHashes|Sort-Object -Unique).Count -eq 3 -and @($v.FrameHashes|Where-Object {$_ -cnotmatch '^[a-f0-9]{64}$'}).Count -eq 0) 'distinct visual observations'
Write-Output "Qualified eight empty-project builds, fourteen review controls, four concurrent repairs, two delegated investigations and native visual actions at $Commit."

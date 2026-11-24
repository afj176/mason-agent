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
Assert-That ($r.Version -eq 1 -and $Commit -cmatch '^[0-9a-f]{40}$' -and $r.Commit -ceq $Commit) 'source identity'
Assert-That ($r.Models.Count -eq 2 -and $r.Models[0].Id -ceq $PrimaryModel -and
    $r.Models[0].Id -ne $r.Models[1].Id -and $r.Models[0].Weights -ne $r.Models[1].Weights) 'two distinct model identities'
Assert-That ($r.Builds.Count -eq 8 -and $r.Controls.Count -eq 14 -and $r.Repairs.Count -eq 2) 'matrix size'
$reviews=@('independent-basket','independent-shelf','independent-receipt')
$buildChecks=@('offer-0','offer-1','offer-2','discount-0','order')+$reviews
$failures=@{working=''; quantity=$reviews[0]; mixed=$reviews[0]; shelf=$reviews[1]; uncleared=$reviews[2]; receipt=$reviews[2]; reused=$reviews[2]}
foreach ($identity in $r.Models) {
    $id=$identity.Id
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
Write-Output "Verified eight empty-project builds, fourteen review controls and two concurrent repairs at $Commit."

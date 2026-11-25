param([Parameter(Mandatory)][string]$Report,[Parameter(Mandatory)][string]$Commit)
$ErrorActionPreference='Stop'
$r=Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
if($r.Version -ne 1 -or $r.Commit -cne $Commit -or [string]::IsNullOrWhiteSpace($r.Model) -or $r.Repetitions -lt 2 -or $r.Repetitions -gt 10){throw 'Model evidence must name this exact source commit and contain repeated trials.'}
$cases=@('working','products','discount','order','fresh','existing','multifile','interrupted')
if($r.Trials.Count -ne $cases.Count*$r.Repetitions){throw 'Incomplete model benchmark matrix'}
foreach($n in (1..$r.Repetitions)){
    foreach($case in $cases){
        $rows=@($r.Trials | Where-Object {$_.Scenario -ceq $case -and $_.Repetition -eq $n})
        if($rows.Count -ne 1){throw "Missing or duplicate evidence for $case trial $n"}
        $row=$rows[0];$negative=$case -in @('products','discount','order')
        if($row.Correct -ne $true -or $row.Calls -lt 1 -or $row.Seconds -le 0 -or $row.OutputTokens -le 0 -or $row.PromptTokens -le 0){throw "Model or oracle failed for $case trial $n"}
        if($negative){
            if($row.State -cne 'Needs verification' -or $row.Completed -or $row.OraclePass -or @($row.FailedFeatures).Count -ne 1 -or $row.FailedFeatures[0] -cne $case){throw "Incorrect broken checkout diagnosis for $case trial $n"}
        }elseif(!$row.Completed -or !$row.OraclePass -or $row.State -cne 'Completed' -or $row.FailedFeatures.Count -ne 0){throw "Incorrect positive control for $case trial $n"}
    }
}
Write-Output "Verified $($r.Trials.Count) real-model benchmark trials for $($r.Model) at $Commit"

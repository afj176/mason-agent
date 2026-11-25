# Creates <payload>\bin\mason.exe, a launcher for the CLI in the payload root.
# The installer adds only this bin folder to PATH, so "mason" works in any terminal without
# putting the application's own libraries on the search path.
# The launcher is the published .NET host with its embedded application path changed from
# "mason.dll" to "..\mason.dll"; it keeps the same icon and version resources.
param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference='Stop'
$root=(Get-Item -LiteralPath $Payload).FullName
$source=Join-Path $root 'mason.exe'
if(-not(Test-Path -LiteralPath $source -PathType Leaf) -or -not(Test-Path -LiteralPath (Join-Path $root 'mason.dll') -PathType Leaf)){throw 'The payload has no published mason CLI.'}
$bytes=[IO.File]::ReadAllBytes($source)
$old=[Text.Encoding]::UTF8.GetBytes('mason.dll')
$new=[Text.Encoding]::UTF8.GetBytes('..\mason.dll')
$matches=New-Object System.Collections.Generic.List[int]
for($i=0;$i -le $bytes.Length-$old.Length-1;$i++){
    if($bytes[$i] -ne $old[0]){continue}
    $same=$true
    for($j=1;$j -lt $old.Length;$j++){if($bytes[$i+$j] -ne $old[$j]){$same=$false;break}}
    # The host stores the path as a zero-terminated UTF-8 string in a zero-padded buffer.
    if($same -and $bytes[$i+$old.Length] -eq 0 -and ($i -eq 0 -or $bytes[$i-1] -eq 0)){$matches.Add($i)}
}
if($matches.Count -ne 1){throw "Expected one embedded application path in mason.exe, found $($matches.Count)."}
$at=$matches[0]
for($k=$old.Length;$k -le $new.Length;$k++){if($bytes[$at+$k] -ne 0){throw 'The embedded application path has no room for the launcher path.'}}
[Array]::Copy($new,0,$bytes,$at,$new.Length)
$bin=Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$launcher=Join-Path $bin 'mason.exe'
[IO.File]::WriteAllBytes($launcher,$bytes)
& $launcher --help | Out-Null
if($LASTEXITCODE -ne 0){throw 'The CLI launcher did not start the CLI.'}
Write-Output "PASS CLI launcher starts the CLI: $launcher"

$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'steam-launch-common.ps1')
$fixture=Join-Path $PSScriptRoot ('runtime/launch-request-isolation-'+[Guid]::NewGuid().ToString('N'))
$control=Join-Path $fixture 'session-next/runtime/control'
[void][IO.Directory]::CreateDirectory($control)
$pending=Join-Path $control 'live-import.json'
$checks=0
function Check($value,$why){if(!$value){throw $why};$script:checks++}
foreach($role in 'Host','Solo','Guest'){
    foreach($operation in 'join-room','return-menu'){
        $generation=[Guid]::NewGuid().ToString('N')
        $request=@{schema=1;generation=$generation;operation=$operation;room='http://127.0.0.1:38477/abcdefghijklmnopqrstuvwx';returnToMenu=$true}|ConvertTo-Json -Compress
        [IO.File]::WriteAllText($pending,$request)
        Write-CoopLaunchMode $fixture $role
        $mode=Get-Content (Join-Path $control 'launch-mode.json') -Raw|ConvertFrom-Json
        Check (!(Test-Path -LiteralPath $pending)) "$role must retire the previous guest $operation request"
        $retired=$pending+'.retired-launch-'+$mode.generation
        Check ((Test-Path -LiteralPath $retired) -and [IO.File]::ReadAllText($retired) -ceq $request) 'Retirement preserves original diagnostic bytes'
        Check ($mode.mode -ceq $role.ToLowerInvariant()) 'Explicit role must be recorded'
    }
}
# Existing local native-load diagnostics deliberately survive a launcher start.
$local=@{schema=1;generation=[Guid]::NewGuid().ToString('N');operation='verify-local-load'}|ConvertTo-Json -Compress
[IO.File]::WriteAllText($pending,$local)
Write-CoopLaunchMode $fixture 'Host'
Check ([IO.File]::ReadAllText($pending) -ceq $local) 'Do not discard an explicitly scheduled local checkpoint verification'
# A Steam replacement game process reads the same launch-mode record. It does
# not call Write-CoopLaunchMode again, so its new handoff must remain intact.
$fresh=@{schema=1;generation=[Guid]::NewGuid().ToString('N');operation='join-room';room='http://127.0.0.1:38477/abcdefghijklmnopqrstuvwx'}|ConvertTo-Json -Compress
[IO.File]::WriteAllText($pending,$fresh)
$launchBefore=[IO.File]::ReadAllText((Join-Path $control 'launch-mode.json'))
for($restart=0;$restart -lt 3;$restart++){
    $record=Get-Content (Join-Path $control 'launch-mode.json') -Raw|ConvertFrom-Json
    Check ($record.mode -eq 'host' -and [IO.File]::ReadAllText($pending) -ceq $fresh) 'Same-launch Steam process restarts preserve their pending handoff'
}
Check ([IO.File]::ReadAllText((Join-Path $control 'launch-mode.json')) -ceq $launchBefore) 'Read-only process restart must not rotate launch generation'
"Launch request isolation checks passed: $checks"

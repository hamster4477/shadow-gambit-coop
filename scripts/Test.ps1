param([string]$Python='python',[switch]$SkipNetwork)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
& (Join-Path $PSScriptRoot 'Test-TwoPlayer.ps1')
$source=Join-Path $repo 'src/multiplayer'
foreach($name in 'test-multi-ownership.ps1','test-multi-spectator.ps1','test-multi-pages.ps1','test-multiplayer-checkpoint-barrier.ps1','test-multiplayer-planning-retire.ps1','test-lobby-host.ps1'){
    & pwsh -NoProfile -File (Join-Path $source "session-next/$name")
    if($LASTEXITCODE -ne 0){throw "Failed: $name"}
}
foreach($name in 'test_lobby_multiplayer.py','session-next/test_multiplayer_session_broker.py'){
    & $Python (Join-Path $source $name)
    if($LASTEXITCODE -ne 0){throw "Failed: $name"}
}
if(!$SkipNetwork){
    & pwsh -NoProfile -File (Join-Path $source 'session-next/test-multi-transport.ps1')
    if($LASTEXITCODE -ne 0){throw 'Local C# transport tests failed.'}
    & $Python (Join-Path $source 'session-next/test_multiplayer_transport.py')
    if($LASTEXITCODE -ne 0){throw 'Local Python transport tests failed.'}
}
$privacy=Join-Path $repo 'tests/test-log-redaction.ps1'
if(Test-Path -LiteralPath $privacy){
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $privacy
    if($LASTEXITCODE -ne 0){throw 'Log redaction tests failed.'}
}
Write-Output 'Selected source tests passed. This does not validate real multiplayer gameplay.'

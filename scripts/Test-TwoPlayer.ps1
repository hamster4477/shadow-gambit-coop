param([switch]$SkipBootstrap,[string]$Python='python')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$source=Join-Path $repo 'src/two-player/session-next'
$tests=@('test-launch-mode.ps1','test-launch-request-isolation.ps1','test-game-process.ps1','test-scene-follow.ps1','test-live-import.ps1','test-enemy-rewind-context.ps1','test-snapshot-transport.ps1','test-udp-parsing.ps1','test-entry-lookup-performance.ps1')
if(!$SkipBootstrap){$tests+='test-bootstrap.ps1'}
foreach($name in $tests){
    & pwsh -NoProfile -File (Join-Path $source $name)
    if($LASTEXITCODE -ne 0){throw "Failed: $name"}
}
& pwsh -NoProfile -File (Join-Path $repo 'src/two-player/test-pursuit.ps1')
if($LASTEXITCODE -ne 0){throw 'Pursuit and observer presentation policy tests failed.'}
foreach($name in 'test-ping-wire.ps1','test-ping-input.ps1','test-ping-audio.ps1'){
    & pwsh -NoProfile -File (Join-Path $repo "src/two-player/$name")
    if($LASTEXITCODE -ne 0){throw "Ping tests failed: $name"}
}
& $Python (Join-Path $repo 'src/two-player/test_ping.py')
if($LASTEXITCODE -ne 0){throw 'Ping broker tests failed.'}
& pwsh -NoProfile -File (Join-Path $repo 'tests/test-steam-account.ps1')
if($LASTEXITCODE -ne 0){throw 'Steam account conversion failed.'}
Write-Output 'Two-player production policy tests passed; native callbacks use test doubles. No game or save was changed.'

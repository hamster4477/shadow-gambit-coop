param([string]$GameDirectory=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
# Read-only environment check. Never downloads, never writes, never starts the game.
$game=[IO.Path]::GetFullPath($GameDirectory)
$coop=Join-Path $game 'ShadowGambitCoop'
$mustFix=New-Object System.Collections.ArrayList

function Report([string]$Name,[string]$State,[string]$Detail){
    $tag=switch($State){'ok'{'OK  '}'warn'{'WARN'}'miss'{'MISS'}default{'????'}}
    Write-Output ("  [{0}] {1,-22} {2}" -f $tag,$Name,$Detail)
}
function Need([string]$Name,[string]$State,[string]$Detail,[string]$Fix){
    Report $Name $State $Detail
    if($State -eq 'miss' -and $Fix){[void]$mustFix.Add($Fix)}
}

Write-Output '环境检查 / Environment check (read-only)'
Write-Output ('游戏目录 / Game directory: ' + $game)
Write-Output ''

$exeOk=Test-Path -LiteralPath (Join-Path $game 'ShadowGambit_TCC.exe') -PathType Leaf
Need 'Game executable' $(if($exeOk){'ok'}else{'miss'}) $(if($exeOk){'found'}else{'ShadowGambit_TCC.exe not found — wrong folder?'}) $(if($exeOk){''}else{'point -GameDirectory at the real game folder'})

$winhttp=Test-Path -LiteralPath (Join-Path $game 'winhttp.dll') -PathType Leaf
$doorstop=Test-Path -LiteralPath (Join-Path $game 'doorstop_config.ini') -PathType Leaf
$coreDll=Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.Core.dll') -PathType Leaf
$bepOk=$winhttp -and $doorstop -and $coreDll
Need 'BepInEx loader' $(if($bepOk){'ok'}else{'miss'}) $(if($bepOk){'winhttp.dll + doorstop_config.ini + BepInEx/core present'}else{'missing — the plugin cannot load at all'}) $(if($bepOk){''}else{'run Install-Dependencies.ps1'})

$pluginOk=Test-Path -LiteralPath (Join-Path $game 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll') -PathType Leaf
Need 'Co-op plugin' $(if($pluginOk){'ok'}else{'miss'}) $(if($pluginOk){'installed'}else{'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll missing'}) $(if($pluginOk){''}else{'extract the co-op release package into the game folder'})

$interopCount=0
$interopDir=Join-Path $game 'BepInEx/interop'
if(Test-Path -LiteralPath $interopDir){$interopCount=@(Get-ChildItem -LiteralPath $interopDir -File -Filter '*.dll').Count}
Need 'IL2CPP interop' $(if($interopCount -gt 0){'ok'}else{'miss'}) $(if($interopCount -gt 0){"$interopCount assemblies"}else{'not generated yet'}) $(if($interopCount -gt 0){''}else{'start the game once with BepInEx installed; it generates these itself'})

$unityCount=0
$unityDir=Join-Path $game 'BepInEx/unity-libs'
if(Test-Path -LiteralPath $unityDir){$unityCount=@(Get-ChildItem -LiteralPath $unityDir -File -Filter '*.dll').Count}

# BepInEx fetches the Unity base libraries itself only when this source keeps its default value.
$source='default'
$cfg=Join-Path $game 'BepInEx/config/BepInEx.cfg'
if(Test-Path -LiteralPath $cfg){
    $text=[IO.File]::ReadAllText($cfg)
    $m=[regex]::Match($text,'(?m)^\s*UnityBaseLibrariesSource\s*=\s*(.*)$')
    if($m.Success -and $m.Groups[1].Value.Trim().Length -eq 0){$source='empty'}
}
if($unityCount -gt 0){
    Report 'Unity base libraries' 'ok' "$unityCount libraries present"
} elseif($source -eq 'empty'){
    Need 'Unity base libraries' 'miss' 'none present, and auto-download is disabled in BepInEx.cfg' 'clear [IL2CPP] UnityBaseLibrariesSource in BepInEx.cfg so BepInEx can fetch them'
} else {
    Report 'Unity base libraries' 'warn' 'none yet — BepInEx downloads them on the first launch (needs network)'
}

$pyLocal=Join-Path $coop 'python/python.exe'
$pyOk=Test-Path -LiteralPath $pyLocal -PathType Leaf
$pyDetail='bundled runtime present'
if(!$pyOk){
    $found=$null
    try{$found=(Get-Command python.exe -ErrorAction Stop).Source}catch{$found=$null}
    if($found){$pyOk=$true;$pyDetail="using PATH runtime: $found"}
    else{$pyDetail='not bundled and python.exe not on PATH — the host cannot open a room'}
}
Need 'Python runtime' $(if($pyOk){'ok'}else{'miss'}) $pyDetail $(if($pyOk){''}else{'run Install-Dependencies.ps1'})

Write-Output ''
if($mustFix.Count -eq 0){
    Write-Output '结果：可以开房并加入。/ Result: ready — you can host and join.'
    if($unityCount -eq 0){Write-Output '首次启动需要联网让 BepInEx 下载基础库。/ The first launch needs network so BepInEx can fetch the base libraries.'}
    exit 0
}
Write-Output '结果：缺少依赖，联机会失败。/ Result: things are missing; co-op will fail.'
Write-Output '需要处理 / To fix:'
foreach($action in $mustFix){Write-Output ('  - ' + $action)}
exit 1

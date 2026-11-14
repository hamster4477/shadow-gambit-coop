param([Parameter(Mandatory=$true)][string]$Room)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'load-preparer.ps1')
if ([GuestSavePrepare]::GameRunning()) { throw '请先退出客机游戏，再接收房主存档。 / Close the guest game before importing the host checkpoint.' }
$rawActive=(Get-ItemProperty -LiteralPath 'HKCU:/Software/Valve/Steam/ActiveProcess').ActiveUser
$active=[int64]$rawActive
if ($active -lt 0) { $active += 4294967296 }
if ($active -le 0 -or $active -gt [uint32]::MaxValue) { throw '请先登录 Steam，再加入联机。 / Sign in to Steam before joining.' }
$steamId=[uint64]76561197960265728+[uint64]$active
$localData=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$account=Join-Path $localData ('ShadowGambit_TCC/user_steam_'+$steamId)
if (!(Test-Path -LiteralPath $account -PathType Container)) { throw '请先用当前 Steam 账号正常启动游戏一次，建立存档档案后再加入。 / Start the game normally once with this Steam account to create a profile, then join.' }
$runtime=Join-Path $PSScriptRoot 'runtime'
[void][IO.Directory]::CreateDirectory($runtime)
. (Join-Path $PSScriptRoot 'load-preparer.ps1')
$result=[GuestPrepareProgress]::Run($Room,$account,$runtime,'1.2.133.r40893.f')
if ($null -eq $result) { throw [OperationCanceledException]::new('已取消加入。 / Join cancelled.') }
[IO.File]::WriteAllText((Join-Path $runtime 'guest-install-result.json'),$result,[Text.UTF8Encoding]::new($false))
Write-Host '房主存档已准备好，将使用第三档案位自动载入。原有第三档案位已备份。 / Host checkpoint ready. Profile slot 3 will load automatically; its previous contents have been backed up.'

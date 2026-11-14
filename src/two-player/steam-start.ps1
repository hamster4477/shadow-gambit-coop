param([string]$ArgumentsFile, [switch]$Setup, [switch]$CheckpointJoin=$true, [switch]$NoDialogs, [ValidateSet('Guest','Host','Solo','Choose')][string]$Role='Guest')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'steam-launch-common.ps1')
$runtime = Join-Path $PSScriptRoot 'session-next/runtime/steam-launch'
$mutex = $null
$owned = $false
$interactive = $Role -eq 'Choose'
$joinChoice = $null
$roomUrl = $null
$gameExitObserved = $false
$collectAfterExit = $false
$previousCollectionOwner = $env:SGC_COLLECT_AFTER_EXIT
try {
    [void][IO.Directory]::CreateDirectory($runtime)
    Write-CoopLaunchProgress $runtime 'started'
    $gameRoot = Split-Path -Parent $PSScriptRoot
    $exe = [IO.Path]::GetFullPath((Join-Path $gameRoot 'ShadowGambit_TCC.exe'))
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw '请将 ShadowGambitCoop 文件夹放在影子诡局的游戏安装目录中。 / Place ShadowGambitCoop inside the game installation folder.' }
    if ($Setup) {
        $launcher = if ($Role -eq 'Choose') { 'Steam-Coop.vbs' } else { 'Steam-Guest.vbs' }
        $option = Get-CoopSteamOption (Join-Path $PSScriptRoot $launcher)
        Set-Clipboard -Value $option
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'Steam-Launch-Option.txt'), $option, [Text.UTF8Encoding]::new($true))
        if (!$NoDialogs) {
            Add-Type -AssemblyName System.Windows.Forms
            [void][Windows.Forms.MessageBox]::Show('启动选项已复制。Steam → 影子诡局 → 属性 → 通用 → 启动选项，粘贴即可。两台电脑各设置一次；清空启动选项可恢复普通启动。

Launch option copied. In Steam, right-click Shadow Gambit → Properties → General → Launch Options, then paste. Set this once on each PC. Launch through Steam to choose Single player, Host or Join. Clear the launch option to restore normal launch.', '影子诡局 · Steam Co-op Setup')
        }
        exit 0
    }
    $items = Read-CoopSteamArguments $ArgumentsFile $runtime
    Write-CoopLaunchProgress $runtime 'arguments-read'
    if ([IO.Path]::GetFullPath($items[0]) -ine $exe) { throw '启动选项对应的游戏路径不一致，请在当前安装目录重新生成启动选项。 / Game path mismatch. Run Steam-Coop.vbs from this installation to regenerate the launch option.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($exe.ToLowerInvariant()))).Replace('-','') } finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Local\ShadowGambitCoopSteamLaunch-'+$key))
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (!$owned) { throw '联机已经在启动或运行，请勿重复启动。 / Co-op is already starting or running.' }
    Write-CoopLaunchProgress $runtime 'launcher-lock-acquired'
    Assert-CoopGameStopped $PSScriptRoot
    if ($Role -eq 'Choose') {
        if ($NoDialogs) { throw '请选择创建房间或加入房间。 / Choose Host or Join.' }
        . (Join-Path $PSScriptRoot 'steam-role.ps1')
        Write-CoopLaunchProgress $runtime 'showing-role-dialog'
        $chosenRole = Select-CoopSteamRole (Join-Path $runtime 'last-role.txt')
        if (!$chosenRole) { Write-CoopLaunchProgress $runtime 'cancelled-role-dialog'; exit 0 }
        $Role = $chosenRole
        Write-CoopLaunchProgress $runtime ('selected-'+$Role)
    }
    $joinNotice=''
    while($true) {
    try {
    if ($interactive -and $Role -eq 'Guest') {
        . (Join-Path $PSScriptRoot 'room-address.ps1')
        . (Join-Path $PSScriptRoot 'steam-join.ps1')
        $addressFile = Join-Path $runtime 'last-address.txt'
        $savedAddress = if (Test-Path -LiteralPath $addressFile) { [IO.File]::ReadAllText($addressFile) } else { '' }
        $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        $rememberedFile = Join-Path $localData 'ShadowGambitCoop/last-room.txt'
        $remembered = if (Test-Path -LiteralPath $rememberedFile) { [IO.File]::ReadAllText($rememberedFile).Trim() } else { '' }
        $udpAddressFile=Join-Path $runtime 'last-udp-address.txt'
        $savedUdp=if(Test-Path -LiteralPath $udpAddressFile){[IO.File]::ReadAllText($udpAddressFile)}else{''}
        $historyFile=Join-Path $runtime 'address-history.json'
        $history=@()
        if(Test-Path -LiteralPath $historyFile){try{$history=@([IO.File]::ReadAllText($historyFile)|ConvertFrom-Json)}catch{}}
        $history=@(Get-CoopAddressHistory $history | Select-Object -First 10)
        $httpsFile=Join-Path $runtime 'last-use-https.txt'
        $savedHttps=(Test-Path -LiteralPath $httpsFile) -and ([IO.File]::ReadAllText($httpsFile).Trim() -eq 'true')
        $joinChoice = Select-CoopJoinAddress $savedAddress $remembered $savedUdp $history $savedHttps $joinNotice
        if (!$joinChoice) { exit 0 }
        $roomUrl = $joinChoice.Room
        [IO.File]::WriteAllText($httpsFile,([bool]$joinChoice.UseHttps).ToString().ToLowerInvariant())
        # Keep valid input even if the network preparation below fails.
        if($joinChoice.Mode -in @('Tunnel','DirectIP','NativeTCP')) {
            [IO.File]::WriteAllText($addressFile,(Get-CoopSavedAddress $joinChoice.Address),[Text.UTF8Encoding]::new($false))
            $udp=if($joinChoice.Mode -in @('Tunnel','NativeTCP')){[string]$joinChoice.UdpAddress}else{''}
            [IO.File]::WriteAllText($udpAddressFile,$udp,[Text.UTF8Encoding]::new($false))
            $storedAddress=Get-CoopSavedAddress $joinChoice.Address
            $history=@(@{Address=$storedAddress;UdpAddress=$udp;UseHttps=[bool]$joinChoice.UseHttps})+@($history|Where-Object {$_.Address -ine $storedAddress}|Select-Object -First 9)
            [IO.File]::WriteAllText($historyFile,(ConvertTo-Json -InputObject @($history) -Depth 3),[Text.UTF8Encoding]::new($false))
        }
    }
    if ($Role -eq 'Solo') {
        Write-CoopLaunchProgress $runtime 'preparing-solo'
        $buildFile = Join-Path $PSScriptRoot 'observation-build.json'
        $plugin = Join-Path $gameRoot 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'
        $build = if (Test-Path -LiteralPath $buildFile) { [IO.File]::ReadAllText($buildFile) | ConvertFrom-Json } else { $null }
        if (!$build -or $build.launchModes -ne $true -or !(Test-Path -LiteralPath $plugin) -or (Get-CoopSha256 $plugin) -ine $build.dllSha256) {
            throw '请安装支持单人入口的完整更新包；仅更新启动脚本还不能停用旧插件的自动连接。 / Install the complete matching update. Updating only the launcher cannot disable an older plugin in single-player mode.'
        }
        $handoffRuntime = Join-Path $PSScriptRoot 'session-next/runtime'
        if (Test-Path -LiteralPath (Join-Path $handoffRuntime 'control/session-load.json')) {
            . (Join-Path $PSScriptRoot 'session-next/load-preparer.ps1')
            [GuestSavePrepare]::AbandonPending($handoffRuntime)
        }
        Write-CoopLaunchMode $PSScriptRoot 'Solo'
        $soloArguments = @()
        if ($items.Count -gt 1) { $soloArguments = @($items[1..($items.Count-1)]) }
        $soloArguments += '--sgc-single-player'
        Write-CoopLaunchProgress $runtime 'starting-solo-game'
        $code = Invoke-CoopSteamGame $exe $soloArguments $gameRoot
        Write-CoopLaunchProgress $runtime ('game-exited-'+$code)
        exit $code
    }
    if($joinChoice -and $joinChoice.Mode -eq 'NativeTCP') {
        if(!('NativeTcpBridge' -as [type])) {Add-Type -Path (Join-Path $PSScriptRoot 'session-next/NativeTcpBridge.cs') -ReferencedAssemblies 'System.Web.Extensions'}
        $nativeBridge=New-Object NativeTcpBridge($joinChoice.Address)
        $roomUrl=$nativeBridge.RoomUrl
        Write-CoopLaunchProgress $runtime 'native-tcp-connected'
    }
    # Do not call steam:// here: Steam would execute this wrapper again.
    $bindAddress = if ($roomUrl) { '' } else { Get-CoopNetworkBinding (Join-Path $runtime 'last-network.txt') }
    Write-CoopLaunchProgress $runtime ('preparing-room-'+$Role)
    $remoteAddress=if($joinChoice -and $joinChoice.Mode -eq 'DirectIP'){[string]$joinChoice.RemoteAddress}else{''}
    & (Join-Path $PSScriptRoot 'start-room.ps1') -Guest:($Role -eq 'Guest') -NoGameStart -CheckpointJoin:$CheckpointJoin -RoomUrl $roomUrl -OwnerProcessId $PID -BindAddress $bindAddress -RemoteAddress $remoteAddress
    Write-CoopLaunchProgress $runtime 'room-ready'
    break
    } catch {
        if(!$interactive -or $Role -ne 'Guest' -or $NoDialogs){throw}
        if($nativeBridge){$nativeBridge.Dispose();$nativeBridge=$null}
        $roomUrl=$null
        $detail=[regex]::Replace($_.Exception.GetBaseException().Message,'(https?://[^/\s]+)/[^\s]*','$1/[room]')
        if($detail.Length -gt 100){$detail=$detail.Substring(0,100)}
        $joinNotice='暂时未连上房间。请等房主创建房间后重试，也可修改地址。 / Room unavailable. Wait for the host or edit the address, then retry.'+[Environment]::NewLine+$detail
        [IO.File]::WriteAllText((Join-Path $runtime 'last-error.txt'),[DateTime]::Now.ToString('s')+[Environment]::NewLine+$detail)
        Write-CoopLaunchProgress $runtime 'join-retry-dialog'
    }
    }

    if($joinChoice) {
        $udp=if($joinChoice.Mode -in @('Tunnel','NativeTCP')){[string]$joinChoice.UdpAddress}else{''}
        $nativeAddress=if($joinChoice.Mode -eq 'NativeTCP'){[string]$joinChoice.Address}else{''}
        $effectiveUdp=Get-CoopEffectiveUdpAddress $udp $nativeAddress
        Write-CoopUdpEndpoint $runtime $roomUrl $effectiveUdp
        if($joinChoice.Mode -in @('Tunnel','NativeTCP')){[IO.File]::WriteAllText($udpAddressFile,$udp,[Text.UTF8Encoding]::new($false))}
    }
    if ($joinChoice -and $joinChoice.Mode -in @('Tunnel','DirectIP','NativeTCP')) { [IO.File]::WriteAllText($addressFile,(Get-CoopSavedAddress $joinChoice.Address),[Text.UTF8Encoding]::new($false)) }
    $gameArguments = @()
    if ($items.Count -gt 1) { $gameArguments = @($items[1..($items.Count-1)]) }
    $collectAfterExit = $Role -eq 'Guest' -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'test-support.ps1'))
    if ($collectAfterExit) { $env:SGC_COLLECT_AFTER_EXIT = '1' }
    $code = Invoke-CoopSteamGame $exe $gameArguments $gameRoot
    $gameExitObserved = $true
    Write-CoopLaunchProgress $runtime ('game-exited-'+$code)
    if ($code -ne 0) { throw ('游戏进程异常结束 / Game exited unexpectedly. Exit code: '+$code+'。诊断记录会自动收集。 / This may occur during launch, loading or gameplay. Diagnostics are collected automatically.') }
    exit $code
} catch {
    Write-CoopLaunchProgress $runtime 'failed'
    $baseError = $_.Exception.GetBaseException()
    if ($baseError.GetType() -eq [OperationCanceledException]) { exit 0 }
    # Room links contain a private token. Do not put it in a shared error log.
    $message = [regex]::Replace($_.Exception.Message, '(https?://[^/\s''"]+)/[^\s''"]*', '$1/[room]' )
    if ($baseError -is [Threading.Tasks.TaskCanceledException]) { $message = '连接房间超时。请确认两端选择相同的联机网络，且房主房间仍开启。 / Connection timed out. Check that both PCs use the same network and the host room is still open.' }
    [IO.File]::WriteAllText((Join-Path $runtime 'last-error.txt'), ([DateTime]::Now.ToString('s') + "`r`n" + $message), [Text.UTF8Encoding]::new($true))
    if (!$NoDialogs) {
        Add-Type -AssemblyName System.Windows.Forms
        $summary = if ($gameExitObserved) { '游戏已关闭，但检测到异常退出。 / The game closed with an unexpected exit.' } elseif ($message.StartsWith('游戏进程检查尚未通过')) { '启动前的游戏进程检查未通过。 / The game process check did not pass.' } else { '联机启动未完成，请检查房间和网络后重试。 / Co-op launch did not finish. Check the room and network, then retry.' }
        [void][Windows.Forms.MessageBox]::Show(($summary + "`r`n`r`n" + $message), '影子诡局 · Steam Co-op')
    }
    Write-Error $message -ErrorAction Continue
    exit 1
} finally {
    if($nativeBridge){$nativeBridge.Dispose()}
    $env:SGC_COLLECT_AFTER_EXIT = $previousCollectionOwner
    if ($collectAfterExit -and $gameExitObserved) {
        try {
            & (Join-Path $PSScriptRoot 'test-support.ps1') -Mode Collect -Role Guest -OnExit | Out-Null
            Write-CoopLaunchProgress $runtime 'exit-report-collected'
        } catch { Write-CoopLaunchProgress $runtime ('exit-report-failed-'+$_.Exception.GetType().Name) }
    }
    if ($owned) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}

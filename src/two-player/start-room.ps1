param([string]$BindAddress, [string]$RemoteAddress, [switch]$PrepareOnly, [switch]$Guest, [string]$RoomUrl, [switch]$CheckpointJoin=$true, [switch]$NoGameStart, [int]$OwnerProcessId=0)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'steam-launch-common.ps1')

Add-Type -AssemblyName System.Net.Http

$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)

if ($CheckpointJoin -and !$PrepareOnly) {
    $buildPath = Join-Path $PSScriptRoot 'observation-build.json'
    $pluginPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'
    if (!(Test-Path -LiteralPath $buildPath) -or !(Test-Path -LiteralPath $pluginPath)) { throw '请先在两台电脑上安装完整联机更新包，再使用自动接档加入。 / Install the complete matching co-op package on both PCs first.' }
    $build = [IO.File]::ReadAllText($buildPath) | ConvertFrom-Json
    if ([Version]$build.version -lt [Version]'0.8.37' -or (Get-CoopSha256 $pluginPath) -ine $build.dllSha256) {
        throw '当前插件不支持这版自动接档，或插件文件与版本记录不一致。请覆盖安装完整更新包。 / Plugin files do not match this build. Install the complete update package.'
    }
    $oldPending = Join-Path $PSScriptRoot 'staging/host-0.8.36.pending'
    if (Test-Path -LiteralPath $oldPending) {
        Move-Item -LiteralPath $oldPending -Destination ($oldPending+'.superseded-'+[Guid]::NewGuid().ToString('N'))
    }
}




function Remove-LegacyTestCommands {

    # ZIP extraction cannot retire files from previous installs. Only remove

    # these exact stock launchers after verifying their released contents.

    $project = Get-Item -LiteralPath $PSScriptRoot

    if ($project.Name -ne 'ShadowGambitCoop' -or ($project.Attributes -band [IO.FileAttributes]::ReparsePoint) -or

        !(Test-Path -LiteralPath (Join-Path $project.Parent.FullName 'ShadowGambit_TCC.exe') -PathType Leaf)) { return }

    $obsolete = @{

        '发送移动测试.cmd' = @('ED0FBBFC4C08EADA3A489016FEA572BE5671052BBF96BE3D50B984842714F072')

        '局域网移动测试.cmd' = @('50EB52140EB623F110D7BDA8D23AABBD9836A12A1B4CBBA1EC5E3DC04F39CAF6','8B2A15CB5A4D2B61B0A539958335C29F8F81A27B1B836563888419771E720F4B')

        '导出本轮日志.cmd' = @('81D3D863413A5FC6FC11A6E37D51AC8A2B8D61E72B6BA7098C1AE0431D2B34B3','7B5FA88D7B7D49F9197AA5403C68E0BB0536CBB7DA8F15E309FD8D39BF90E77F')

    }

    foreach ($name in $obsolete.Keys) {

        $path = Join-Path $project.FullName $name

        try {

            $file = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue

            if (!$file -or $file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }

            if ($obsolete[$name] -contains (Get-CoopSha256 $path)) {

                Remove-Item -LiteralPath $path

            }

        } catch { Write-Warning ('保留旧脚本：' + $name) }

    }

}

if (!$PrepareOnly) { Remove-LegacyTestCommands }

# If an installed host DLL was locked during preparation, complete the verified

# update on the next normal launch after the game closes.

$pendingUpdate = Join-Path $PSScriptRoot 'staging/host-0.8.36.pending'

if (!$PrepareOnly -and (Test-Path -LiteralPath $pendingUpdate)) {

    if (Get-CoopGameProcess) {

        throw '0.8.36 补丁已准备好。请正常退出游戏后再点一次开房，当前对局将保留。'

    }

    $package = Join-Path $PSScriptRoot 'ShadowGambitCoop-Update-0.8.36.zip'

    if ((Get-CoopSha256 $package) -ne [IO.File]::ReadAllText($pendingUpdate).Trim()) { throw '待安装补丁校验失败。' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zip = [IO.Compression.ZipFile]::OpenRead($package)

    try {

        foreach ($entryName in 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll','ShadowGambitCoop/双机测试-0.8.36.txt','ShadowGambitCoop/observation-build.json') {

            $target = Join-Path (Split-Path -Parent $PSScriptRoot) $entryName

            $temp = $target+'.installing'

            [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($entryName),$temp,$true)

            if ([IO.File]::Exists($target)) { [IO.File]::Copy($temp,$target,$true); [IO.File]::Delete($temp) }

            else { [IO.File]::Move($temp,$target) }

        }

    } finally { $zip.Dispose() }

    [IO.File]::Delete($pendingUpdate)

    Write-Host '房主 0.8.36 已安装。'

}

if (!$BindAddress) {

    $links = @(Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |

        Sort-Object { $_.NetIPv4Interface.InterfaceMetric })

    foreach ($link in $links) {

        $candidate = @($link.IPv4Address.IPAddress | Where-Object { $_ -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' })

        if ($candidate.Count -eq 1) { $BindAddress = $candidate[0]; break }

    }

}

if (!$BindAddress -and !$Guest) { throw '请先连上热点或局域网，再重新开房。 / Connect to a network, then create the room again.' }

$addr = if ($BindAddress) { [Net.IPAddress]::Parse($BindAddress) } else { $null }

if ($addr -and !(Test-CoopNetworkIPv4 $addr.IPAddressToString)) { throw '请选择本机局域网或组网网卡的 IPv4。 / Select the IPv4 address of your LAN or virtual LAN adapter.' }
if ($RemoteAddress -and !(Test-CoopNetworkIPv4 $RemoteAddress)) { throw '房主虚拟 IP 不在支持的组网地址范围内。 / Host virtual IP is outside supported LAN ranges.' }



$selectedInterface = if ($addr) { Get-NetIPAddress -AddressFamily IPv4 -IPAddress $addr.IPAddressToString -ErrorAction Stop | Select-Object -First 1 } else { $null }
$prefix = if ($selectedInterface) { $selectedInterface.PrefixLength } else { 0 }

function Find-LanRoom {

    $bytes = $addr.GetAddressBytes()

    $broadcast = [byte[]]$bytes.Clone()

    for ($i=0; $i -lt 4; $i++) {

        $bits = [Math]::Max(0,[Math]::Min(8,$prefix-8*$i))

        $broadcast[$i] = $bytes[$i] -bor ((1 -shl (8-$bits))-1)

    }

    $target = [Net.IPEndPoint]::new([Net.IPAddress]::new($broadcast),38478)
    if($RemoteAddress){$target=[Net.IPEndPoint]::new([Net.IPAddress]::Parse($RemoteAddress),38478)}

    $udp = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new($addr,0))

    $rooms = @{}

    try {

        $udp.EnableBroadcast = $true

        $udp.Client.ReceiveTimeout = 350

        $nonce = [Guid]::NewGuid().ToString('N')

        $packet = [Text.Encoding]::ASCII.GetBytes('SGC_FIND '+$nonce)
        $directTargets = @(Get-CoopDirectDiscoveryTargets $addr $prefix ([string]$selectedInterface.InterfaceAlias))
        if($RemoteAddress){$directTargets=@()}

        for ($round=0; $round -lt 4; $round++) {

            [void]$udp.Send($packet,$packet.Length,$target)
            if ($round -ge 1 -and $rooms.Count -eq 0) {
                foreach ($direct in $directTargets) {
                    $endpoint = [Net.IPEndPoint]::new([Net.IPAddress]::Parse($direct),38478)
                    try { [void]$udp.Send($packet,$packet.Length,$endpoint) } catch [Net.Sockets.SocketException] { }
                }
            }

            $timer = [Diagnostics.Stopwatch]::StartNew()

            while ($timer.ElapsedMilliseconds -lt 500) {

                $peer = [Net.IPEndPoint]::new([Net.IPAddress]::Any,0)

                try { $answer = [Text.Encoding]::ASCII.GetString($udp.Receive([ref]$peer)) }

                catch [Net.Sockets.SocketException] {
                    if ($_.Exception.SocketErrorCode -eq [Net.Sockets.SocketError]::ConnectionReset) { continue }
                    break
                }

                if ($peer.Port -ne 38478 -or $answer -cnotmatch ('^SGC_ROOM '+$nonce+' ([A-Za-z0-9_-]{24})$')) { continue }

                $secret = $Matches[1]

                $remote = $peer.Address.GetAddressBytes()

                $same = $true

                for ($i=0; $i -lt 4; $i++) {

                    $bits = [Math]::Max(0,[Math]::Min(8,$prefix-8*$i))

                    if (($bytes[$i] -shr (8-$bits)) -ne ($remote[$i] -shr (8-$bits))) { $same=$false; break }

                }

                if($RemoteAddress){$same=($peer.Address.IPAddressToString -eq $RemoteAddress)}

                if ($same) { $rooms['http://'+$peer.Address+':38477/'+$secret]=$true }

            }

        }

        $rooms.Keys

    } finally { $udp.Dispose() }

}

if ($Guest) {
    $dir = Join-Path $localData 'ShadowGambitCoop'
    # Preserve an array even when discovery emits exactly one URL. An array
    # inside an if branch is unrolled by PowerShell; indexing it then reads 'h'.
    $found = @(if (!$RoomUrl -and $addr) { Find-LanRoom })
    if ($found.Count -gt 1) { throw '发现多个房间。请只让目标房主开启配对窗口后再试。 / Multiple rooms found. Keep only the intended host pairing window open.' }
    if($RemoteAddress -and !$found.Count){throw ('房主虚拟 IP '+$RemoteAddress+' 未回应搜房。请确认所选组网网卡和房主房间已开启。 / No discovery reply. Check the selected adapter and host room.')}
    $url = if ($RoomUrl) { $RoomUrl.Trim() } elseif ($found.Count) { $found[0] } else { ([string](Get-Clipboard -Raw -ErrorAction SilentlyContinue)).Trim() }
    $pattern = '^https?://[^/?#\\\s%@]+/[A-Za-z0-9_-]{24}$'
    $remembered = Join-Path $dir 'last-room.txt'
    if ($url -cnotmatch $pattern -and (Test-Path -LiteralPath $remembered)) { $url = [IO.File]::ReadAllText($remembered).Trim() }
    $uri = $null
    if ($url -cnotmatch $pattern -or ![Uri]::TryCreate($url,[UriKind]::Absolute,[ref]$uri) -or $uri.UserInfo -or $uri.Port -lt 1) {
        throw '未找到房间。请确认房主已开房；也可以返回地址页填写公网或组网地址。 / No room found. Make sure the host has opened a room, or enter its public or virtual LAN address.'
    }
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    try {
        $response = $null
        try {
            try { $response = $client.GetAsync($url+'/health').GetAwaiter().GetResult() }
            catch {
                $cause = $_.Exception
                $tlsFailure = $false
                while ($cause) {
                    if ($cause -is [Security.Authentication.AuthenticationException] -or
                        ($cause -is [Net.WebException] -and $cause.Status -in @([Net.WebExceptionStatus]::TrustFailure,[Net.WebExceptionStatus]::SecureChannelFailure))) { $tlsFailure=$true }
                    $cause=$cause.InnerException
                }
                if ($tlsFailure) {
                    throw ('HTTPS 证书或握手校验失败：'+$uri.Host+':'+$uri.Port+'。樱花自动 HTTPS 默认可能使用自签名证书；请在樱花「子域绑定」为 TCP 隧道绑定域名，自动 HTTPS 设为「自动」，重启隧道并等待证书加载后，改用绑定的域名:端口加入。')
                }
                $details = [Collections.Generic.List[string]]::new()
                $cause = $_.Exception
                while ($cause) {
                    $detail = $cause.GetType().Name + ': ' + $cause.Message
                    $detail = [regex]::Replace($detail, 'https?://[^\s''"]+', '[room address]')
                    if($cause -is [Net.Sockets.SocketException]) { $detail += ' [SocketError=' + $cause.SocketErrorCode + ']' }
                    $details.Add($detail)
                    $cause = $cause.InnerException
                }
                throw ('房间连接失败：'+$uri.Scheme+'://'+$uri.Host+':'+$uri.Port+"`r`n"+($details -join "`r`n"))
            }
            if (!$response.IsSuccessStatusCode) {
                $status = [int]$response.StatusCode
                $destination = $uri.Host+':'+$uri.Port
                if ($status -eq 501 -and $response.Headers.Server.ToString() -match 'SakuraFrp') {
                    throw ("樱花节点 $destination 拦截了明文 HTTP。请为 TCP 隧道开启自动 HTTPS，重启隧道，并在加入房间时填写 https://$destination（完整链接保留原房间标识）。UDP 隧道无需更改。")
                }
                if ($status -eq 404) {
                    throw ("房间检查失败：$destination 返回 HTTP 404。请重新复制当前房主的完整房间链接，检查隧道是否指向本次房间。")
                }
                throw ("房间检查失败：$destination 返回 HTTP $status，尚未启动游戏。请核对 TCP 隧道的本地目标为 127.0.0.1:38477，并确认房主已创建房间；此错误发生在 TCP 房间检查，不是 UDP 状态同步。")
            }
            $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } finally { if ($response) { $response.Dispose() } }
        if ($health -cnotmatch '^COOP_ROOM_084 [0-9A-F]{64}$') { throw '房主网关版本过旧或插件校验失败。请两端覆盖安装同一份完整更新包，并重新创建房间。 / Host gateway or plugin mismatch. Update both PCs with the same package and recreate the room.' }
        if (!$PrepareOnly -and (!$build -or $health.Substring(14) -ine $build.dllSha256)) { throw '房主与客机插件版本不一致。请两端覆盖安装同一份完整更新包，再加入房间。 / Plugin versions differ. Install the same complete package on both PCs.' }
    } finally { $client.Dispose() }
    if ($PrepareOnly) { Write-Output 'Discovery and HTTP health verified'; return }
    [void][IO.Directory]::CreateDirectory($dir)
    $liveLobbyJoin = $CheckpointJoin -and $build.liveLobbyJoin -eq $true
    if ($liveLobbyJoin) {
        Assert-CoopGameStopped $PSScriptRoot
        . (Join-Path $PSScriptRoot 'session-next/load-preparer.ps1')
        [GuestSavePrepare]::AbandonPending((Join-Path $PSScriptRoot 'session-next/runtime'),$url)
        foreach ($obsolete in @((Join-Path $PSScriptRoot 'session-next/runtime/control/host-start.txt'),(Join-Path $dir 'join-once.txt'))) {
            if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete }
        }
        Write-Host '房间已验证。进入游戏后跟随房主选关，并自动接收任务存档。 / Room verified. The guest will follow the host and receive the checkpoint automatically.'
    } elseif ($CheckpointJoin) {
        & (Join-Path $PSScriptRoot 'session-next/prepare-guest.ps1') -Room $url
    } else {
        [IO.File]::WriteAllText((Join-Path $dir 'join-once.txt'),$url)
        Write-Host '已验证房间。进入同一任务后自动加入。 / Room verified. Joining automatically in the matching mission.'
    }
    [IO.File]::WriteAllText($remembered,$url)
    if ($liveLobbyJoin) { Write-CoopLaunchMode $PSScriptRoot 'Guest' $url }
    else { Write-CoopLaunchMode $PSScriptRoot 'Guest' }
    if (!$NoGameStart -and !(Get-CoopGameProcess)) { Start-Process 'steam://rungameid/1545560' }
    return
}


# Selecting Host abandons a prior unfinished guest startup request. An implicit
# game shutdown now preserves requests so Steam may restart without losing them.
$handoffRuntime = Join-Path $PSScriptRoot 'session-next/runtime'
if (!$PrepareOnly -and !(Get-CoopGameProcess) -and
    (Test-Path -LiteralPath (Join-Path $handoffRuntime 'control/session-load.json'))) {
    . (Join-Path $PSScriptRoot 'session-next/load-preparer.ps1')
    [GuestSavePrepare]::AbandonPending($handoffRuntime)
}

$dir = Join-Path $localData 'ShadowGambitCoop'

$roomDirectory = Join-Path $dir 'rooms'

$control = Join-Path $PSScriptRoot 'room-control'

[void][IO.Directory]::CreateDirectory($control)

$nonce = [guid]::NewGuid().ToString('N')

$requestPath = Join-Path $control ($addr.IPAddressToString+'.request')

$ackPath = Join-Path $control ($addr.IPAddressToString+'.ack')

function Get-Listeners {

    Get-NetTCPConnection -State Listen -LocalPort 38477 -ErrorAction SilentlyContinue |

        Where-Object { $_.LocalAddress -in @($addr.IPAddressToString, '0.0.0.0', '::') }

}

# Publish the selected address atomically before requesting the UDP port.

$selected = Join-Path $control 'selected-address'

$tempSelected = $selected + '.' + $nonce

[IO.File]::WriteAllText($tempSelected,$addr.IPAddressToString,[Text.Encoding]::ASCII)

try { if ([IO.File]::Exists($selected)) { [IO.File]::Replace($tempSelected,$selected,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($tempSelected,$selected) } }

catch [IO.IOException] { if ([IO.File]::Exists($tempSelected) -and [IO.File]::Exists($selected)) { [IO.File]::Replace($tempSelected,$selected,[System.Management.Automation.Language.NullString]::Value) } else { throw } }

$gatewayArguments = '-u "{0}" --bind {1} --room-directory "{2}"' -f (Join-Path $PSScriptRoot 'lan_remote.py'),$addr,$roomDirectory
if ($OwnerProcessId -gt 0) {
    if ($OwnerProcessId -ne $PID -or !$NoGameStart) { throw 'Gateway lifetime must belong to the current Steam launcher.' }
    $gatewayArguments += ' --owner-pid '+$OwnerProcessId
}

function Start-LanGateway {

    $gatewayStart = New-Object Diagnostics.ProcessStartInfo
    $embeddedPython = Join-Path $PSScriptRoot 'python/python.exe'
    $gatewayStart.FileName = if(Test-Path -LiteralPath $embeddedPython){$embeddedPython}else{(Get-Command python.exe -ErrorAction Stop).Source}
    $gatewayStart.Arguments = $gatewayArguments
    $gatewayStart.WorkingDirectory = $PSScriptRoot
    $gatewayStart.UseShellExecute = $false
    $gatewayStart.CreateNoWindow = $true
    $gatewayProcess = [Diagnostics.Process]::Start($gatewayStart)
    $gatewayProcess.Dispose()

}

function Get-RoomDigest([string]$token) {

    $sha = [Security.Cryptography.SHA256]::Create()

    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($token))).Replace('-','') }

    finally { $sha.Dispose() }

}

if (!@(Get-Listeners).Count) {

    Start-LanGateway

}

$timer = [Diagnostics.Stopwatch]::StartNew()

[IO.File]::WriteAllText($requestPath,"$nonce $prefix",[Text.Encoding]::ASCII)

$verified = $false

$upgradeDigest = $null

while ($timer.Elapsed.TotalSeconds -lt 30) {

    Start-Sleep -Milliseconds 200

    if (!(Test-Path -LiteralPath $ackPath)) { continue }

    $ack = [IO.File]::ReadAllText($ackPath)

    if ($ack -cmatch ('^'+$nonce+' ([0-9]+) ([0-9a-f]{64})( READY(?:13|12|11|10|[123456789]))?$')) {

        $gatewayId = [int]$Matches[1]

        $digest = $Matches[2]

        $current = $Matches[3] -eq ' READY13'

        $listeners = @(Get-Listeners)

        $verified = $listeners.Count -eq 1 -and $listeners[0].OwningProcess -eq $gatewayId

        if (!$verified) { continue }

        if ($current) {

            if ($upgradeDigest -and $digest -ne $upgradeDigest) { throw '更新后房间标识发生了变化，请保留窗口排查。' }

            break

        }

        $verified = $false

        if ($upgradeDigest) { continue }

        # Fresh nonce + listener ownership prove the process answered this install.

        # Pin its handle and check its exact Python command line before stopping it.

        $old = [Diagnostics.Process]::GetProcessById($gatewayId)

        try {

            [void]$old.Handle

            $info = Get-CimInstance Win32_Process -Filter ('ProcessId='+$gatewayId)

            $expected = [regex]::Escape($gatewayArguments)

            if ($info.Name -notin @('python.exe','pythonw.exe') -or

                $info.CommandLine -notmatch ('^\s*(?:"[^"]+"|[^"\s]+)\s+'+$expected+'\s*$') -or $old.HasExited) {

                throw "端口 38477 的 PID $gatewayId 未通过旧网关身份核验，请保留窗口排查。"

            }

            $roomFile = Join-Path $roomDirectory ($addr.IPAddressToString+'.room')

            $saved = if (Test-Path -LiteralPath $roomFile) { [IO.File]::ReadAllText($roomFile).Trim() }

            if (!$saved -or (Get-RoomDigest $saved) -ne $digest) {

                # An earlier launcher can have written its room file in a different

                # Windows context. Recover only this address and its proven live token.

                $foundRoom = @(Find-LanRoom | Where-Object {

                    $uri = [uri]$_

                    $uri.Host -eq $addr.IPAddressToString -and (Get-RoomDigest ($uri.AbsolutePath.TrimStart('/'))) -eq $digest

                })

                if ($foundRoom.Count -ne 1) { throw '旧房间标识未能核对。为保留客机配对，已保留当前网关。' }

                [void][IO.Directory]::CreateDirectory($roomDirectory)

                [IO.File]::WriteAllText($roomFile,([uri]$foundRoom[0]).AbsolutePath.TrimStart('/'),[Text.Encoding]::ASCII)

            }

            if (![IO.File]::ReadAllText($requestPath).StartsWith($nonce)) { Write-Host '请使用最后打开的开房窗口。'; return }

            $upgradeDigest = $digest

            $old.Kill()

            if (!$old.WaitForExit(5000)) { throw '旧网关未能及时退出。' }

        } finally { $old.Dispose() }

        Start-LanGateway

        $timer.Restart()

    }

}

if (!$verified) {

    $latest = [IO.File]::ReadAllText($requestPath)

    if (!$latest.StartsWith($nonce)) { Write-Host '已有更新的开房请求，请使用最后打开的窗口。'; return }

    $tcp = (@(Get-Listeners).OwningProcess -join ',')

    $udp = (@(Get-NetUDPEndpoint -LocalPort 38478 -ErrorAction SilentlyContinue).OwningProcess -join ',')

    throw "等待配对确认超过 30 秒。热点 $addr，网关 PID [$tcp]，搜房 UDP 监听 PID [$udp]。"

}

if ($PrepareOnly) { Write-Output "Gateway $gatewayId confirmed pairing on $addr"; return }

Write-Host '房间已就绪。房间运行期间，客机可使用局域网自动加入。 / Room ready. Guests can use LAN auto-join while it is running.'

# Optional address copy. Opening the pairing window does not require a saved URL.

try {

    $secret = [IO.File]::ReadAllText((Join-Path $roomDirectory ($addr.IPAddressToString+'.room'))).Trim()

    $hash = Get-RoomDigest $secret

    if ($hash -eq $digest) {

        $url = 'http://'+$addr.IPAddressToString+':38477/'+$secret

        [IO.File]::WriteAllText((Join-Path $dir '当前房间.txt'),$url)

        Set-Clipboard -Value $url

    }

} catch { Write-Host '可以直接使用客机一键加入。 / You can use the guest quick-join launcher.' }

$hostControl=Join-Path $PSScriptRoot 'session-next/runtime/control'
[void][IO.Directory]::CreateDirectory($hostControl)
[IO.File]::WriteAllText((Join-Path $hostControl 'host-start.txt'),[Guid]::NewGuid().ToString('N'))
Write-CoopLaunchMode $PSScriptRoot 'Host'
$oldGuestIntent = Join-Path $localData 'ShadowGambitCoop/join-once.txt'
if (Test-Path -LiteralPath $oldGuestIntent) { Move-Item -LiteralPath $oldGuestIntent -Destination ($oldGuestIntent+'.retired-host-'+[Guid]::NewGuid().ToString('N')) }

if (!$NoGameStart -and !(Get-CoopGameProcess)) { Start-Process 'steam://rungameid/1545560' }




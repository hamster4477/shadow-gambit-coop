function Resolve-CoopRoomAddress([string]$Address, [string]$RememberedRoom) {
    $inputAddress = $Address.Trim().TrimEnd('/')
    if (!$inputAddress) { throw '请输入内网穿透域名和端口。 / Enter the public host:port.' }
    # Explicit TCP means the existing HTTP protocol over a plain TCP tunnel.
    $inputAddress = $inputAddress -replace '^tcp://','http://'
    if ($inputAddress -notmatch '^https?://') {
        $scheme = 'http://'
        $inputAddress = $scheme + $inputAddress
    }
    $uri = $null
    if ($inputAddress -notmatch '^https?://[^/?#\\\s%@]+(?:/[A-Za-z0-9_-]{24})?$' -or ![Uri]::TryCreate($inputAddress,[UriKind]::Absolute,[ref]$uri) -or
        $uri.Scheme -notin @('http','https') -or $uri.UserInfo -or $uri.Port -lt 1 -or $uri.HostNameType -notin @('Dns','IPv4')) {
        throw '地址格式不正确。请填 域名:端口，例如 example.com:57556；也可以粘贴完整房间链接。 / Enter host:port or a complete room link.'
    }
    if ($uri.AbsolutePath -cmatch '^/[A-Za-z0-9_-]{24}$') { return $uri.GetLeftPart([UriPartial]::Authority) + $uri.AbsolutePath }
    if ($uri.AbsolutePath -ne '/') { throw '完整房间链接需要保留域名后面的房间标识；请重新复制完整链接。 / Copy the complete room link, including the room ID after the host name.' }
    $remembered = $null
    if ([Uri]::TryCreate($RememberedRoom,[UriKind]::Absolute,[ref]$remembered) -and $remembered.Scheme -in @('http','https') -and
        !$remembered.UserInfo -and !$remembered.Query -and !$remembered.Fragment -and $remembered.AbsolutePath -cmatch '^/[A-Za-z0-9_-]{24}$') {
        return $uri.GetLeftPart([UriPartial]::Authority) + $remembered.AbsolutePath
    }
    throw '首次连接这台房主还需要房间标识。请粘贴完整房间链接，连接后会记住。 / This legacy HTTPS connection needs the full room link on first use. Paste the host invitation here.'
}

function Resolve-CoopUdpEndpoint([string]$Address) {
    $value=$Address.Trim() -replace '^udp://',''
    if (!$value) { return '' }
    $uri=$null
    if($value -notmatch '^[A-Za-z0-9.-]+:[0-9]{1,5}$' -or ![Uri]::TryCreate(('udp://'+$value),[UriKind]::Absolute,[ref]$uri) -or $uri.Port -lt 1 -or $uri.Port -gt 65535) {
        throw 'UDP 地址请填樱花 UDP 隧道的 域名:公网端口，不要填写房间标识。 / UDP requires the public host:port of the UDP tunnel, without a room ID.'
    }
    return $uri.Host+':'+$uri.Port
}
function Get-CoopEffectiveUdpAddress([string]$Address,[string]$NativeAddress) {
    if($Address){return Resolve-CoopUdpEndpoint $Address}
    if(!$NativeAddress){return ''}
    $endpoint=[Uri]$NativeAddress
    return Resolve-CoopUdpEndpoint ($endpoint.Host+':'+$endpoint.Port)
}
function Write-CoopUdpEndpoint([string]$Runtime,[string]$Room,[string]$Address) {
    $target=Join-Path $Runtime 'udp-endpoint.txt'
    $pending=$target+'.new'
    $value=Resolve-CoopUdpEndpoint $Address
    $text=''
    if($value) {
        $endpoint=[Uri]('udp://'+$value)
        try {
            $lookup=[Net.Dns]::GetHostAddressesAsync($endpoint.Host)
            if(!$lookup.Wait(3000)){throw 'DNS timeout'}
            $ip=@($lookup.Result | Where-Object AddressFamily -eq ([Net.Sockets.AddressFamily]::InterNetwork)) | Select-Object -First 1
            if(!$ip){throw 'No IPv4 address'}
            $text=([Uri]$Room).GetLeftPart([UriPartial]::Authority)+"`n"+$ip.IPAddressToString+':'+$endpoint.Port
        } catch { Write-Warning 'UDP 域名暂时无法解析，本次使用 TCP 状态同步；下次启动会重新解析。 / UDP DNS lookup failed. Using TCP for now; the next launch will retry.' }
    }
    [IO.File]::WriteAllText($pending,$text,[Text.UTF8Encoding]::new($false))
    if([IO.File]::Exists($target)){[IO.File]::Replace($pending,$target,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($pending,$target)}
}

function Redeem-CoopInvite([string]$Invitation) {
    if($Invitation -notmatch '^SGC1 (https://[^\s]+) ([0-9]{8}) (\S+)$'){throw '邀请格式不正确，请完整复制房主的邀请。 / Invalid invitation. Copy the complete host invitation.'}
    $endpoint=[Uri]$Matches[1];$code=$Matches[2];$udp=$Matches[3]
    if($endpoint.Scheme -ne 'https' -or $endpoint.UserInfo -or $endpoint.AbsolutePath -ne '/' -or $endpoint.Query -or $endpoint.Fragment){throw '邀请中的房间地址不正确。 / Invalid room address in the invitation.'}
    $udp=if($udp -eq '-') {''} else {Resolve-CoopUdpEndpoint $udp}
    Add-Type -AssemblyName System.Net.Http
    $handler=New-Object Net.Http.HttpClientHandler;$handler.UseProxy=$false;$handler.AllowAutoRedirect=$false
    $client=New-Object Net.Http.HttpClient($handler);$client.Timeout=[TimeSpan]::FromSeconds(8)
    $body=New-Object Net.Http.StringContent($code);$response=$null
    try {
        $response=$client.PostAsync($endpoint.GetLeftPart([UriPartial]::Authority)+'/coop/pair',$body).GetAwaiter().GetResult()
        if(!$response.IsSuccessStatusCode){throw '邀请已失效、输入错误，或房主网关尚未更新。请房主重新创建邀请；连续输错后等待一分钟再试。 / Invitation invalid, expired, or host gateway outdated. Ask for a new invitation. After repeated failures, wait one minute.'}
        $result=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if($result -cnotmatch '^COOP_PAIR_1 ([A-Za-z0-9_-]{24})$'){throw '房主返回的配对结果格式不正确。 / Invalid pairing response from host.'}
        return @{Room=$endpoint.GetLeftPart([UriPartial]::Authority)+'/'+$Matches[1];UdpAddress=$udp}
    } finally {if($response){$response.Dispose()};$body.Dispose();$client.Dispose()}
}

function Get-CoopAddressHistory([object[]]$Entries,[int]$Depth=0) {
    if($Depth -gt 6){return}
    foreach($entry in $Entries) {
        if($null -eq $entry -or $entry -is [string]){continue}
        if($entry.Address -is [string] -and $entry.Address.Length -gt 0 -and $entry.Address.Length -le 2048) {
            [pscustomobject]@{Address=(Get-CoopSavedAddress ([string]$entry.Address));UdpAddress=[string]$entry.UdpAddress;UseHttps=([bool]$entry.UseHttps -or [string]$entry.Address -match '^https://')}
        } elseif($entry.value) { Get-CoopAddressHistory @($entry.value) ($Depth+1) }
        elseif($entry -is [array]) { Get-CoopAddressHistory $entry ($Depth+1) }
    }
}

function Set-CoopAddressScheme([string]$Address,[bool]$UseHttps) {
    $value=$Address.Trim() -replace '^(https?|tcp)://',''
    $prefix=if($UseHttps){'https://'}else{'tcp://'}
    return $prefix+$value
}

# History stores endpoints, not a transport scheme or private room token.
function Get-CoopSavedAddress([string]$Address) {
    $value=$Address.Trim() -replace '^(https?|tcp)://',''
    return ($value -split '/',2)[0]
}
